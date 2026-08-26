using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TestEnvironmentPreparationProcessSafetyTests
{
    [Fact]
    public void PreparationScript_BuildsAndStagesPublishBeforeFinalRuntimeMutation()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var mainStart = source.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            StringComparison.Ordinal);
        Assert.True(mainStart >= 0);

        var main = source[mainStart..];
        var buildEnvironmentPreflight = main.IndexOf(
            "Enter-IsolatedBuildEnvironmentPreflightLease `",
            StringComparison.Ordinal);
        var buildEnvironmentInitialization = main.IndexOf(
            "Initialize-IsolatedBuildEnvironmentOnD `",
            StringComparison.Ordinal);
        var buildEnvironmentRevalidation = main.IndexOf(
            "Assert-IsolatedBuildEnvironmentPreflightLease `",
            buildEnvironmentInitialization,
            StringComparison.Ordinal);
        var build = main.IndexOf(
            "'build',",
            StringComparison.Ordinal);
        var outputRootCreation = main.IndexOf(
            "New-Item -ItemType Directory -Force -Path $OutputRoot",
            StringComparison.Ordinal);
        var privateStage = main.IndexOf(
            "New-IsolatedRuntimePromotionWorkspace -OutputRoot $OutputRoot",
            StringComparison.Ordinal);
        var publishLease = main.IndexOf(
            "$publishCacheLease =",
            privateStage,
            StringComparison.Ordinal);
        var desktopPublish = main.IndexOf(
            "'publish', $desktopProject",
            publishLease,
            StringComparison.Ordinal);
        var serverPublish = main.IndexOf(
            "'publish', $serverProject",
            desktopPublish,
            StringComparison.Ordinal);
        var publishLeaseRelease = main.IndexOf(
            "$publishCacheLease.Dispose()",
            serverPublish,
            StringComparison.Ordinal);
        var exclusiveLease = main.IndexOf(
            "Enter-PreparationGateLease -Path $preparationGateLeasePath",
            StringComparison.Ordinal);
        var invalidation = main.IndexOf(
            "-Reason 'preparation-started'",
            exclusiveLease,
            StringComparison.Ordinal);
        var stop = main.IndexOf(
            "Stop-IsolatedRuntimeProcesses -OutputRoot $OutputRoot",
            invalidation,
            StringComparison.Ordinal);
        var runtimeLifetimeExclusion = main.IndexOf(
            "$preparationLease = [IO.File]::Open(",
            stop,
            StringComparison.Ordinal);
        var stagedData = main.IndexOf(
            "Invoke-TestEnvironmentPreparationFaultPoint -Point 'data:before'",
            runtimeLifetimeExclusion,
            StringComparison.Ordinal);
        var promotion = main.IndexOf(
            "Invoke-IsolatedRuntimeComponentPromotion `",
            stagedData,
            StringComparison.Ordinal);

        Assert.InRange(
            buildEnvironmentPreflight,
            0,
            buildEnvironmentInitialization - 1);
        Assert.InRange(
            buildEnvironmentInitialization,
            buildEnvironmentPreflight + 1,
            buildEnvironmentRevalidation - 1);
        Assert.InRange(
            buildEnvironmentRevalidation,
            buildEnvironmentInitialization + 1,
            build - 1);
        Assert.InRange(build, buildEnvironmentRevalidation + 1, outputRootCreation - 1);
        Assert.InRange(outputRootCreation, build + 1, privateStage - 1);
        Assert.InRange(privateStage, outputRootCreation + 1, publishLease - 1);
        Assert.InRange(publishLease, privateStage + 1, desktopPublish - 1);
        Assert.InRange(desktopPublish, publishLease + 1, serverPublish - 1);
        Assert.InRange(serverPublish, desktopPublish + 1, publishLeaseRelease - 1);
        Assert.InRange(publishLeaseRelease, serverPublish + 1, exclusiveLease - 1);
        Assert.InRange(exclusiveLease, publishLeaseRelease + 1, invalidation - 1);
        Assert.InRange(invalidation, exclusiveLease + 1, stop - 1);
        Assert.Contains(
            ".georaeplan-prepare-gate.lock",
            main[..stop],
            StringComparison.Ordinal);
        Assert.True(
            runtimeLifetimeExclusion > stop,
            "Preparation must stop the existing runtime before acquiring its lifetime exclusion.");
        Assert.True(stagedData > runtimeLifetimeExclusion);
        Assert.True(promotion > stagedData);
        Assert.Contains(
            "function Enter-PlainDirectoryAncestorChainLease",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Enter-IsolatedBuildCacheLeafMutationLease",
            source,
            StringComparison.Ordinal);
        var buildEnvironmentInitializationFunction = source[
            source.IndexOf(
                "function Initialize-IsolatedBuildEnvironmentOnD",
                StringComparison.Ordinal)..source.IndexOf(
                "function Initialize-TestEnvironmentFinalPathNativeMethods",
                StringComparison.Ordinal)];
        Assert.DoesNotContain(
            "New-Item",
            buildEnvironmentInitializationFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "provisioned before preparation",
            buildEnvironmentInitializationFunction,
            StringComparison.Ordinal);
        var cacheMutationLeaseFunction = source[
            source.IndexOf(
                "function Enter-IsolatedBuildCacheLeafMutationLease",
                StringComparison.Ordinal)..source.IndexOf(
                "function Enter-IsolatedBuildEnvironmentPreflightLease",
                StringComparison.Ordinal)];
        Assert.Contains(
            "[IO.FileMode]::Open",
            cacheMutationLeaseFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OpenOrCreate",
            cacheMutationLeaseFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Unsafe isolated build cache:",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Remove-Item -LiteralPath $appOutput",
            main[privateStage..promotion],
            StringComparison.Ordinal);

        var runAllTemplateStart = source.IndexOf(
            "$runAllPsContent = @'",
            StringComparison.Ordinal);
        var runAllTemplateEnd = source.IndexOf(
            "'@",
            runAllTemplateStart + 1,
            StringComparison.Ordinal);
        var runAll = source[runAllTemplateStart..runAllTemplateEnd];
        var runAllLease = runAll.IndexOf(
            "$runtimeLease = [IO.File]::Open(",
            StringComparison.Ordinal);
        var postLeaseInvalidation = runAll.IndexOf(
            "if (Test-Path -LiteralPath $invalidMarkerPath)",
            runAllLease + 1,
            StringComparison.Ordinal);
        Assert.True(
            postLeaseInvalidation > runAllLease,
            "Run-All must recheck invalidation after acquiring its shared lease.");
    }

    [Fact]
    public void PreparationScript_SourceAppRootUsesPlainAncestorIdentityLeaseAcrossCopy()
    {
        var source = File.ReadAllText(ResolvePreparationScript());

        Assert.Contains(
            "function Enter-SourceAppRootIdentityLease",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SourceAppRoot path contains a reparse point",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Assert-SourceAppRootIdentityLease",
            source,
            StringComparison.Ordinal);

        var mainStart = source.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            StringComparison.Ordinal);
        var mainPreflight = source.IndexOf(
            "$sourceAppRootPreflightLease =",
            mainStart,
            StringComparison.Ordinal);
        var buildEnvironment = source.IndexOf(
            "Initialize-IsolatedBuildEnvironmentOnD",
            mainStart,
            StringComparison.Ordinal);
        Assert.InRange(mainPreflight, mainStart + 1, buildEnvironment - 1);

        var copyStart = source.IndexOf(
            "function Copy-CurrentAppSnapshot",
            StringComparison.Ordinal);
        var copyEnd = source.IndexOf(
            "function Reset-IsolatedServerStorage",
            copyStart,
            StringComparison.Ordinal);
        Assert.True(copyStart >= 0 && copyEnd > copyStart);
        var copy = source[copyStart..copyEnd];
        var lease = copy.IndexOf(
            "Enter-SourceAppRootIdentityLease",
            StringComparison.Ordinal);
        var beforeCopy = copy.IndexOf(
            "Assert-SourceAppRootIdentityLease",
            lease + 1,
            StringComparison.Ordinal);
        var robocopy = copy.IndexOf(
            "Invoke-RobocopyMirror",
            StringComparison.Ordinal);
        var afterCopy = copy.IndexOf(
            "Assert-SourceAppRootIdentityLease",
            robocopy + 1,
            StringComparison.Ordinal);
        Assert.InRange(lease, 0, beforeCopy - 1);
        Assert.InRange(beforeCopy, lease + 1, robocopy - 1);
        Assert.True(afterCopy > robocopy);
        Assert.Contains(
            "$sourceRootIdentityLease.Dispose()",
            copy,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceAppRootIdentityLease_RejectsRootAndAncestorJunctions(
        bool junctionIsAncestor)
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"source-root-identity-{Guid.NewGuid():N}");
        var physicalRoot = Path.Combine(testRoot, "physical");
        var physicalChild = Path.Combine(physicalRoot, "app-data");
        var junctionRoot = Path.Combine(testRoot, "source-junction");
        var sourceRoot = junctionIsAncestor
            ? Path.Combine(junctionRoot, "app-data")
            : junctionRoot;
        var harnessPath = Path.Combine(testRoot, "source-root-identity.ps1");

        Directory.CreateDirectory(physicalChild);
        CreateDirectoryJunction(
            junctionRoot,
            junctionIsAncestor ? physicalRoot : physicalChild);
        try
        {
            File.WriteAllText(
                harnessPath,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$SourceRoot
                )
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$errors)
                if ($errors.Count -ne 0) {
                    throw (($errors | ForEach-Object Message) -join "`n")
                }
                foreach ($name in @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'ConvertTo-NormalizedFullPath',
                    'Enter-SourceAppRootIdentityLease'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $name
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$name function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }
                $lease = Enter-SourceAppRootIdentityLease -Path $SourceRoot
                try {
                    throw 'The SourceAppRoot junction was accepted.'
                }
                finally {
                    if ($null -ne $lease) { $lease.Dispose() }
                }
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(20),
                "-SourceScript",
                sourceScript,
                "-SourceRoot",
                sourceRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "SourceAppRoot path contains a reparse point",
                result.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(junctionRoot))
                Directory.Delete(junctionRoot, recursive: false);
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task GeneratedRunAll_FailsClosedWhilePreparationExclusionIsHeld()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-runall-exclusion-{Guid.NewGuid():N}");
        var runtimeRoot = Path.Combine(testRoot, "runtime");
        var harnessPath = Path.Combine(testRoot, "generate.ps1");
        Directory.CreateDirectory(testRoot);
        try
        {
            File.WriteAllText(
                harnessPath,
                """
                param([string]$SourceScript, [string]$OutputRoot, [string]$PowerShellPath)
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) { throw 'parse failed' }
                foreach ($name in @(
                    'New-Utf8NoBomEncoding',
                    'New-Utf8BomEncoding',
                    'Write-Utf8File',
                    'Set-RuntimeInvalidationMarker',
                    'Write-TestRunScripts'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $name
                    }, $true)
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }
                Write-TestRunScripts `
                    -OutputRoot $OutputRoot `
                    -DefaultBaseUrl 'http://127.0.0.1:19080' `
                    -DotnetExe $PowerShellPath `
                    -CertificationId 'prepare-exclusion-test' `
                    -CertificationMode 'test' `
                    -PasswordResetCount 0 `
                    -IncludeInternalLockProbe
                """,
                new UTF8Encoding(false));
            var powerShellPath = ResolveWindowsPowerShellPath();
            var generation = await RunPowerShellAsync(
                powerShellPath,
                harnessPath,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolvePreparationScript(),
                "-OutputRoot",
                runtimeRoot,
                "-PowerShellPath",
                powerShellPath);
            Assert.Equal(0, generation.ExitCode);

            File.Delete(Path.Combine(
                runtimeRoot,
                ".georaeplan-runtime-invalid"));
            File.WriteAllText(
                Path.Combine(runtimeRoot, ".georaeplan-runtime-ready"),
                "fixture-ready");
            await using var preparationLease = File.Open(
                Path.Combine(runtimeRoot, ".georaeplan-prepare-gate.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            var runAll = await RunPowerShellAsync(
                powerShellPath,
                Path.Combine(runtimeRoot, "Run-All.ps1"),
                TimeSpan.FromSeconds(20));
            Assert.NotEqual(0, runAll.ExitCode);
            Assert.Contains(
                "Preparation is already starting for this isolated runtime root",
                runAll.Stdout + runAll.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationGateLease_ContentionFailsBeforeInvalidation()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-gate-contention-{Guid.NewGuid():N}");
        var gatePath = Path.Combine(testRoot, ".georaeplan-prepare-gate.lock");
        var invalidPath = Path.Combine(testRoot, ".georaeplan-runtime-invalid");
        var harnessPath = Path.Combine(testRoot, "prepare-gate-contention.ps1");
        Directory.CreateDirectory(testRoot);
        try
        {
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [string]$SourceScript,
                    [string]$GatePath,
                    [string]$InvalidPath
                )
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$errors)
                if ($errors.Count -ne 0) { throw 'parse failed' }
                $functionAst = $ast.Find({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq 'Enter-PreparationGateLease'
                }, $true)
                . ([scriptblock]::Create($functionAst.Extent.Text))
                $lease = Enter-PreparationGateLease `
                    -Path $GatePath `
                    -Attempts 2 `
                    -RetryDelayMilliseconds 10
                try {
                    [IO.File]::WriteAllText($InvalidPath, 'invalid')
                }
                finally {
                    $lease.Dispose()
                }
                """,
                new UTF8Encoding(false));

            await using var ownerLease = File.Open(
                gatePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(20),
                "-SourceScript",
                ResolvePreparationScript(),
                "-GatePath",
                gatePath,
                "-InvalidPath",
                invalidPath);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "같은 OutputRoot를 사용 중입니다",
                result.Stderr,
                StringComparison.Ordinal);
            Assert.False(File.Exists(invalidPath));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public void PreparationScript_ProtectsLauncherAndPromotesOnlyManagedComponents()
    {
        var sourceScript = ResolvePreparationScript();
        var source = File.ReadAllText(sourceScript);

        var cleanupFunctionIndex = source.IndexOf(
            "function Stop-IsolatedRuntimeProcesses",
            StringComparison.Ordinal);
        var protectedPidSetIndex = source.IndexOf(
            "$protectedProcessIds = [Collections.Generic.HashSet[int]]::new()",
            cleanupFunctionIndex,
            StringComparison.Ordinal);
        var ancestorWalkIndex = source.IndexOf(
            "$ancestorProcessId = [int]$PID",
            cleanupFunctionIndex,
            StringComparison.Ordinal);
        var ancestorAdvanceIndex = source.IndexOf(
            "$ancestorProcessId = [int]$processById[$ancestorProcessId].ParentProcessId",
            ancestorWalkIndex,
            StringComparison.Ordinal);
        var protectedPidExclusionIndex = source.IndexOf(
            "-not $protectedProcessIds.Contains([int]$_.ProcessId)",
            cleanupFunctionIndex,
            StringComparison.Ordinal);
        var volumeRootGuardIndex = source.IndexOf(
            "OutputRoot must be below the volume root",
            cleanupFunctionIndex,
            StringComparison.Ordinal);
        var pathBoundaryIndex = source.IndexOf(
            "$outputRootPrefix = $normalizedOutputRoot + [IO.Path]::DirectorySeparatorChar",
            cleanupFunctionIndex,
            StringComparison.Ordinal);
        var entryPointMatcherIndex = source.IndexOf(
            "function Test-IsolatedRuntimeHostCommandLine",
            cleanupFunctionIndex,
            StringComparison.Ordinal);
        var entryPointMatchCallIndex = source.IndexOf(
            "Test-IsolatedRuntimeHostCommandLine `",
            entryPointMatcherIndex + 1,
            StringComparison.Ordinal);
        var cleanupCallIndex = source.IndexOf(
            "Stop-IsolatedRuntimeProcesses -OutputRoot $OutputRoot",
            StringComparison.Ordinal);
        var publishIndex = source.IndexOf(
            "'publish', $desktopProject",
            StringComparison.Ordinal);
        var promotionIndex = source.IndexOf(
            "Invoke-IsolatedRuntimeComponentPromotion `",
            cleanupCallIndex,
            StringComparison.Ordinal);

        Assert.True(cleanupFunctionIndex >= 0, "The runtime cleanup function was not found.");
        Assert.True(
            protectedPidSetIndex > cleanupFunctionIndex,
            "The cleanup function does not initialize its protected PID set.");
        Assert.True(
            ancestorWalkIndex > protectedPidSetIndex,
            "The cleanup function does not start its protected ancestry walk at the current process.");
        Assert.True(
            ancestorAdvanceIndex > ancestorWalkIndex,
            "The cleanup function does not walk beyond the immediate parent process.");
        Assert.True(
            protectedPidExclusionIndex > ancestorAdvanceIndex,
            "The cleanup query does not exclude the current process and its ancestors.");
        Assert.True(
            volumeRootGuardIndex > cleanupFunctionIndex,
            "The cleanup function does not reject an unsafe volume-root scope.");
        Assert.True(
            pathBoundaryIndex > volumeRootGuardIndex,
            "Executable matching does not establish a directory boundary.");
        Assert.True(
            entryPointMatcherIndex > pathBoundaryIndex,
            "Command-line matching does not identify runtime entry points.");
        Assert.True(
            entryPointMatchCallIndex > entryPointMatcherIndex,
            "The cleanup query does not use runtime entry-point matching.");
        Assert.True(cleanupCallIndex >= 0, "The preparation workflow does not invoke runtime cleanup.");
        Assert.True(
            publishIndex >= 0 && publishIndex < cleanupCallIndex,
            "Private staged publishing must finish before the final runtime is stopped.");
        Assert.True(
            promotionIndex > cleanupCallIndex,
            "Managed component promotion must start only after runtime cleanup and exclusion.");
        Assert.Contains(
            "Get-IsolatedRuntimeManagedComponents",
            source[cleanupCallIndex..promotionIndex],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[IO.Directory]::Move($OutputRoot",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationScript_LaunchesRunAllWithBoundedQualifiedHandshake()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var launchStart = source.IndexOf(
            "if ($launchAfterPreparation) {",
            StringComparison.Ordinal);

        Assert.True(launchStart >= 0, "The post-preparation launch block was not found.");
        var launchBlock = source[launchStart..];
        Assert.Contains(
            "$runAllProcess = Start-Process `",
            launchBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "-FilePath (Join-Path $OutputRoot 'Run-All.cmd')",
            launchBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "-WindowStyle Hidden",
            launchBlock,
            StringComparison.Ordinal);
        Assert.Contains("-PassThru", launchBlock, StringComparison.Ordinal);
        Assert.Contains(
            "$runAllProcess.WaitForExit(1500)",
            launchBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "$runAllProcess.ExitCode -ne 0",
            launchBlock,
            StringComparison.Ordinal);
        var nonzeroExitIndex = launchBlock.IndexOf(
            "$runAllProcess.ExitCode -ne 0",
            StringComparison.Ordinal);
        var earlyExitThrowIndex = launchBlock.IndexOf(
            "throw (",
            nonzeroExitIndex,
            StringComparison.Ordinal);
        var earlyExitMessageIndex = launchBlock.IndexOf(
            "로컬 테스트 실행 프로세스가 조기 종료되었습니다.",
            earlyExitThrowIndex,
            StringComparison.Ordinal);
        Assert.True(
            earlyExitThrowIndex > nonzeroExitIndex &&
            earlyExitMessageIndex > earlyExitThrowIndex,
            "The early nonzero exit does not throw the generic Korean error.");
        Assert.Contains(
            "로컬 테스트 실행 프로세스가 조기 종료되었습니다.",
            launchBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "프로세스 시작만 확인했습니다.",
            launchBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "최종 서버/앱 상태는 RuntimeLogs에서 확인하세요.",
            launchBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "로컬 테스트 서버/앱 실행을 시작했습니다.",
            launchBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content", launchBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllText", launchBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$runScopedAdminPassword",
            launchBlock,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(launchBlock, "$runAllProcess.ExitCode"));
        Assert.Contains(
            "$runAllProcess.Dispose()",
            launchBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationScript_GeneratesAsciiOnlyHiddenManualLauncherAndUsageGuide()
    {
        var repositoryRoot = FindRepositoryRoot();
        var preparationSource = File.ReadAllText(ResolvePreparationScript());
        var runtimeRoot = Path.Combine(
            repositoryRoot,
            "테스트 시행",
            "실행환경");
        var launcherPath = Path.Combine(runtimeRoot, "Launch-Test-App.vbs");
        var guidePath = Path.Combine(runtimeRoot, "Launcher-README.txt");

        Assert.True(File.Exists(launcherPath), $"Hidden launcher not found: {launcherPath}");
        Assert.True(File.Exists(guidePath), $"Launcher guide not found: {guidePath}");

        var launcherBytes = File.ReadAllBytes(launcherPath);
        var launcherSource = Encoding.ASCII.GetString(launcherBytes);
        Assert.All(launcherBytes, value => Assert.InRange(value, (byte)0, (byte)127));
        Assert.Contains("Option Explicit", launcherSource, StringComparison.Ordinal);
        var resumeNextIndex = launcherSource.IndexOf(
            "On Error Resume Next",
            StringComparison.Ordinal);
        var shellCreationIndex = launcherSource.IndexOf(
            "Set shell = CreateObject(\"WScript.Shell\")",
            StringComparison.Ordinal);
        var fileSystemCreationIndex = launcherSource.IndexOf(
            "Set fileSystem = CreateObject(\"Scripting.FileSystemObject\")",
            StringComparison.Ordinal);
        Assert.True(
            resumeNextIndex >= 0 &&
            shellCreationIndex > resumeNextIndex &&
            fileSystemCreationIndex > shellCreationIndex,
            "Initialization is not protected from raw Windows Script Host errors.");
        Assert.Contains(
            "fileSystem.GetParentFolderName(WScript.ScriptFullName)",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileSystem.BuildPath(scriptDirectory, \"Run-All.cmd\")",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "shell.ExpandEnvironmentStrings(\"%ComSpec%\")",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "readyToLaunch = Len(Trim(comSpec)) > 0",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "readyToLaunch = fileSystem.FileExists(comSpec)",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "readyToLaunch = fileSystem.FileExists(runAllPath)",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "If Not initializationFailed And readyToLaunch Then",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains("initializationFailed = True", launcherSource, StringComparison.Ordinal);
        Assert.Contains(
            "Set processEnvironment = shell.Environment(\"PROCESS\")",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "suppressionName = \"GEORAEPLAN_SUPPRESS_FAILURE_DIALOG\"",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "For Each environmentEntry In processEnvironment",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "previousSuppressionValue = Mid(environmentEntry, Len(suppressionName) + 2)",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "processEnvironment(suppressionName) = \"1\"",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains("suppressionWasApplied = True", launcherSource, StringComparison.Ordinal);
        Assert.Contains(
            "processEnvironment(suppressionName) = previousSuppressionValue",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "processEnvironment.Remove suppressionName",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "command = Quote(comSpec) & \" /d /c \" & Quote(Quote(runAllPath))",
            launcherSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "exitCode = shell.Run(command, 0, True)",
            launcherSource,
            StringComparison.Ordinal);
        var launchIndex = launcherSource.IndexOf(
            "exitCode = shell.Run(command, 0, True)",
            StringComparison.Ordinal);
        var suppressionSetIndex = launcherSource.IndexOf(
            "processEnvironment(suppressionName) = \"1\"",
            StringComparison.Ordinal);
        var suppressionRestoreIndex = launcherSource.IndexOf(
            "processEnvironment(suppressionName) = previousSuppressionValue",
            StringComparison.Ordinal);
        var suppressionRemoveIndex = launcherSource.IndexOf(
            "processEnvironment.Remove suppressionName",
            StringComparison.Ordinal);
        var errorResetIndex = launcherSource.IndexOf(
            "On Error GoTo 0",
            StringComparison.Ordinal);
        var genericMessageIndex = launcherSource.IndexOf(
            "MsgBox ",
            StringComparison.Ordinal);
        Assert.True(
            suppressionSetIndex > fileSystemCreationIndex &&
            launchIndex > suppressionSetIndex &&
            suppressionRestoreIndex > launchIndex &&
            suppressionRemoveIndex > suppressionRestoreIndex &&
            genericMessageIndex > launchIndex &&
            genericMessageIndex > suppressionRemoveIndex &&
            errorResetIndex > genericMessageIndex,
            "The child-only failure-dialog suppression or generic error boundary is incomplete.");
        Assert.Equal(
            1,
            CountOccurrences(
                launcherSource,
                "suppressionName = \"GEORAEPLAN_SUPPRESS_FAILURE_DIALOG\""));
        Assert.Contains("If exitCode <> 0 Then", launcherSource, StringComparison.Ordinal);
        Assert.Contains("RuntimeLogs", launcherSource, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(launcherSource, "MsgBox "));
        Assert.DoesNotContain("ReadAllText", launcherSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-Content", launcherSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", launcherSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", launcherSource, StringComparison.OrdinalIgnoreCase);

        const string launcherTemplateMarker = "$hiddenLauncherContent = @'";
        var launcherTemplateMarkerIndex = preparationSource.IndexOf(
            launcherTemplateMarker,
            StringComparison.Ordinal);
        Assert.True(launcherTemplateMarkerIndex >= 0, "The hidden launcher template was not found.");
        var launcherTemplateStart = preparationSource.IndexOf(
            '\n',
            launcherTemplateMarkerIndex) + 1;
        var launcherTemplateEnd = preparationSource.IndexOf(
            "\n'@",
            launcherTemplateStart,
            StringComparison.Ordinal);
        Assert.True(
            launcherTemplateStart > 0 && launcherTemplateEnd > launcherTemplateStart,
            "The hidden launcher template boundary was not found.");
        var launcherTemplate = preparationSource[
            launcherTemplateStart..launcherTemplateEnd];
        Assert.All(
            Encoding.UTF8.GetBytes(launcherTemplate),
            value => Assert.InRange(value, (byte)0, (byte)127));
        Assert.Equal(
            launcherTemplate.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
            launcherSource.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

        var nonzeroConditionIndex = launcherSource.IndexOf(
            "If exitCode <> 0 Then",
            StringComparison.Ordinal);
        var messageBoxIndex = launcherSource.IndexOf(
            "MsgBox ",
            nonzeroConditionIndex,
            StringComparison.Ordinal);
        var nonzeroConditionEndIndex = launcherSource.IndexOf(
            "End If",
            nonzeroConditionIndex,
            StringComparison.Ordinal);
        Assert.True(
            nonzeroConditionIndex >= 0 &&
            messageBoxIndex > nonzeroConditionIndex &&
            nonzeroConditionEndIndex > messageBoxIndex,
            "The single generic error dialog is not bounded by the nonzero exit condition.");

        Assert.Contains(
            launcherTemplateMarker,
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Write-Utf8File -Path (Join-Path $OutputRoot 'Launch-Test-App.vbs')",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Write-Utf8File -Path (Join-Path $OutputRoot 'Launcher-README.txt')",
            preparationSource,
            StringComparison.Ordinal);

        var guide = File.ReadAllText(guidePath);
        Assert.Contains("Launch-Test-App.vbs를 더블클릭", guide, StringComparison.Ordinal);
        Assert.Contains("CMD 창 없이", guide, StringComparison.Ordinal);
        Assert.Contains("진단 또는 동기식 실행", guide, StringComparison.Ordinal);
        Assert.Contains("Run-All.cmd", guide, StringComparison.Ordinal);
        Assert.Contains("RuntimeLogs", guide, StringComparison.Ordinal);

        var rootReadme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        var testReadme = File.ReadAllText(Path.Combine(repositoryRoot, "테스트 시행", "README.md"));
        foreach (var documentation in new[] { rootReadme, testReadme })
        {
            Assert.Contains("Launch-Test-App.vbs", documentation, StringComparison.Ordinal);
            Assert.Contains("CMD 창 없이", documentation, StringComparison.Ordinal);
            Assert.Contains("Run-All.cmd", documentation, StringComparison.Ordinal);
            Assert.Contains("자동화·진단", documentation, StringComparison.Ordinal);
        }
        foreach (var documentation in new[] { rootReadme, testReadme })
        {
            Assert.DoesNotContain(
                "`Run-All.cmd`만 정상 실행 진입점",
                documentation,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "`Run-All.cmd` 단일 진입점",
                documentation,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "단일 진입점",
                documentation,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "반드시 `Run-All.cmd`",
                documentation,
                StringComparison.Ordinal);
        }

        const string guideTemplateMarker = "$launcherReadmeContent = @\"";
        var guideTemplateMarkerIndex = preparationSource.IndexOf(
            guideTemplateMarker,
            StringComparison.Ordinal);
        Assert.True(guideTemplateMarkerIndex >= 0, "The launcher guide template was not found.");
        var guideTemplateStart = preparationSource.IndexOf(
            '\n',
            guideTemplateMarkerIndex) + 1;
        var guideTemplateEnd = preparationSource.IndexOf(
            "\n\"@",
            guideTemplateStart,
            StringComparison.Ordinal);
        Assert.True(
            guideTemplateStart > 0 && guideTemplateEnd > guideTemplateStart,
            "The launcher guide template boundary was not found.");
        var guideTemplate = preparationSource[guideTemplateStart..guideTemplateEnd];
        Assert.Equal(
            guideTemplate.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
            guide.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());

        var launchStart = preparationSource.IndexOf(
            "if ($launchAfterPreparation) {",
            StringComparison.Ordinal);
        Assert.True(launchStart >= 0, "The direct automatic launch block was not found.");
        var automaticLaunchBlock = preparationSource[launchStart..];
        Assert.Contains(
            "-FilePath (Join-Path $OutputRoot 'Run-All.cmd')",
            automaticLaunchBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "$runAllProcess.WaitForExit(1500)",
            automaticLaunchBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-FilePath (Join-Path $OutputRoot 'Launch-Test-App.vbs')",
            automaticLaunchBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationScript_GeneratedRunAllKillsOnlyCapturedProcessObjects()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var stopAndDisposeStart = source.IndexOf(
            "function Stop-AndDisposeRuntimeProcess",
            StringComparison.Ordinal);
        var stopAppStart = source.IndexOf(
            "function Stop-RuntimeAppAfterServerFailure",
            stopAndDisposeStart,
            StringComparison.Ordinal);
        var nextFunctionStart = source.IndexOf(
            "function Start-HiddenServerProcess",
            stopAppStart,
            StringComparison.Ordinal);

        Assert.True(
            stopAndDisposeStart >= 0 &&
            stopAppStart > stopAndDisposeStart &&
            nextFunctionStart > stopAppStart,
            "The generated Run-All cleanup helpers were not found.");

        var stopAndDisposeBlock = source[stopAndDisposeStart..stopAppStart];
        var stopAppBlock = source[stopAppStart..nextFunctionStart];
        foreach (var cleanupBlock in new[] { stopAndDisposeBlock, stopAppBlock })
        {
            Assert.Contains(
                "[System.Diagnostics.Process]$Process",
                cleanupBlock,
                StringComparison.Ordinal);
            Assert.Contains("$Process.Kill()", cleanupBlock, StringComparison.Ordinal);
            Assert.Contains(
                "$Process.WaitForExit(5000)",
                cleanupBlock,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Stop-Process", cleanupBlock, StringComparison.Ordinal);
            Assert.DoesNotContain("-Id $Process.Id", cleanupBlock, StringComparison.Ordinal);
        }

        Assert.Contains("finally {", stopAndDisposeBlock, StringComparison.Ordinal);
        Assert.Contains(
            "$Process.Dispose()",
            stopAndDisposeBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationScript_GeneratesHiddenCredentialSafePrimaryLaunchers()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var runAllTemplateStart = source.IndexOf(
            "$runAllPsContent = @'",
            StringComparison.Ordinal);
        var runAllTemplateEnd = source.IndexOf(
            "$runAllContent = @\"",
            runAllTemplateStart,
            StringComparison.Ordinal);

        Assert.True(
            runAllTemplateStart >= 0 && runAllTemplateEnd > runAllTemplateStart,
            "The generated Run-All PowerShell template was not found.");
        AssertRunAllAutoLoginContract(
            source[runAllTemplateStart..runAllTemplateEnd]);

        Assert.Contains(
            "\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" " +
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass " +
            "-WindowStyle Hidden -File \"%~dp0Run-IsolatedComponent.ps1\" " +
            "-Mode Server",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if not \"%RUN_EXIT%\"==\"0\" pause",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass " +
            "-WindowStyle Hidden -File \"%RUN_ALL_PS%\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationScript_UsesSingleKestrelEndpointConfiguration()
    {
        var source = File.ReadAllText(ResolvePreparationScript());

        Assert.DoesNotContain(
            "'ASPNETCORE_URLS' =",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$env:ASPNETCORE_URLS =",
            source,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            CountOccurrences(
                source,
                "Kestrel__Endpoints__Http__Url"));
    }

    [Fact]
    public async Task PreparationScript_SourceApiGuardRejectsRemoteByDefaultAndAllowsLoopback()
    {
        var sourceScript = ResolvePreparationScript();
        var source = File.ReadAllText(sourceScript);
        var guardStart = source.IndexOf(
            "function Assert-SafeSourceApiBaseUrl",
            StringComparison.Ordinal);
        var guardEnd = source.IndexOf(
            "function Initialize-TestEnvironmentFinalPathNativeMethods",
            guardStart,
            StringComparison.Ordinal);
        var guardCall = source.IndexOf(
            "$sourceApiBaseUrl = Assert-SafeSourceApiBaseUrl",
            guardEnd,
            StringComparison.Ordinal);
        var dotnetResolution = source.IndexOf(
            "$dotnetExe = Resolve-DotnetCommand",
            guardCall,
            StringComparison.Ordinal);

        Assert.True(
            guardStart >= 0 && guardEnd > guardStart,
            "The source API safety guard was not found.");
        Assert.True(
            guardCall > guardEnd,
            "The preparation workflow does not invoke the source API guard.");
        Assert.True(
            dotnetResolution > guardCall,
            "The source API guard must run before build, publish, or cleanup.");

        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-source-api-guard-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(
            testRoot,
            "source-api-guard.ps1");
        Directory.CreateDirectory(testRoot);
        try
        {
            var guardSource = source[guardStart..guardEnd];
            var harness = guardSource +
                          """

                          $ErrorActionPreference = 'Stop'
                          $remoteRejected = $false
                          try {
                              Assert-SafeSourceApiBaseUrl `
                                  -BaseUrl 'https://api.example.invalid' |
                                  Out-Null
                          }
                          catch {
                              if ($_.Exception.Message -match
                                  'Remote SourceApiBaseUrl is blocked') {
                                  $remoteRejected = $true
                              }
                              else {
                                  throw
                              }
                          }

                          if (-not $remoteRejected) {
                              throw 'Remote source API was accepted without opt-in.'
                          }

                          $loopback = Assert-SafeSourceApiBaseUrl `
                              -BaseUrl 'http://127.0.0.1:19080/'
                          if ($loopback -ne 'http://127.0.0.1:19080') {
                              throw "Unexpected loopback normalization: $loopback"
                          }

                          $remote = Assert-SafeSourceApiBaseUrl `
                              -BaseUrl 'https://api.example.invalid/' `
                              -AllowRemote
                          if ($remote -ne 'https://api.example.invalid') {
                              throw "Unexpected remote normalization: $remote"
                          }

                          foreach ($unsafeCase in @(
                              @{
                                  Url = 'http://api.example.invalid'
                                  AllowRemote = $true
                                  Pattern = 'must use HTTPS'
                                  Secret = ''
                              },
                              @{
                                  Url = 'https://user:credential-secret@api.example.invalid'
                                  AllowRemote = $true
                                  Pattern = 'cannot contain user information'
                                  Secret = 'credential-secret'
                              },
                              @{
                                  Url = 'https://api.example.invalid/?token=query-secret'
                                  AllowRemote = $true
                                  Pattern = 'cannot contain user information'
                                  Secret = 'query-secret'
                              },
                              @{
                                  Url = 'https://api.example.invalid/#fragment-secret'
                                  AllowRemote = $true
                                  Pattern = 'cannot contain user information'
                                  Secret = 'fragment-secret'
                              }
                          )) {
                              $unsafeRejected = $false
                              try {
                                  Assert-SafeSourceApiBaseUrl `
                                      -BaseUrl $unsafeCase.Url `
                                      -AllowRemote:$unsafeCase.AllowRemote |
                                      Out-Null
                              }
                              catch {
                                  $message = $_.Exception.Message
                                  if ($message -notmatch $unsafeCase.Pattern) {
                                      throw
                                  }
                                  if (
                                      -not [string]::IsNullOrEmpty(
                                          $unsafeCase.Secret) -and
                                      $message.Contains($unsafeCase.Secret)
                                  ) {
                                      throw 'A rejected source URL secret was echoed.'
                                  }
                                  $unsafeRejected = $true
                              }
                              if (-not $unsafeRejected) {
                                  throw "Unsafe source API was accepted."
                              }
                          }

                          Write-Output 'source_api_guard_ok'
                          """;
            File.WriteAllText(
                harnessPath,
                harness,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(30));

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "source_api_guard_ok",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "credential-secret",
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "query-secret",
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "fragment-secret",
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public void PreparationScript_RedactsCredentialChildFailureAndPinsBuildCachesToD()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var credentialsStart = source.IndexOf(
            "function ConvertFrom-StoredCredentialEnvelopeProcessResult",
            StringComparison.Ordinal);
        var credentialsEnd = source.IndexOf(
            "function Get-SourceUsersFromApi",
            credentialsStart,
            StringComparison.Ordinal);
        var credentialsSource = source[credentialsStart..credentialsEnd];
        var buildEnvironmentCall = source.IndexOf(
            "Initialize-IsolatedBuildEnvironmentOnD",
            credentialsEnd,
            StringComparison.Ordinal);
        var dotnetResolution = source.IndexOf(
            "$dotnetExe = Resolve-DotnetCommand",
            buildEnvironmentCall,
            StringComparison.Ordinal);
        var firstBuild = source.IndexOf(
            "'build',",
            dotnetResolution,
            StringComparison.Ordinal);
        var firstPublish = source.IndexOf(
            "'publish'",
            dotnetResolution,
            StringComparison.Ordinal);

        Assert.True(credentialsStart >= 0 && credentialsEnd > credentialsStart);
        Assert.Contains(
            "stored_credentials_child_output_redacted=True",
            credentialsSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$result.Text",
            credentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "D:\\DevCaches\\georaeplan-v1-prepare",
            source,
            StringComparison.Ordinal);
        Assert.Contains("NUGET_PACKAGES", source, StringComparison.Ordinal);
        Assert.Contains("NUGET_HTTP_CACHE_PATH", source, StringComparison.Ordinal);
        Assert.Contains("NUGET_PLUGINS_CACHE_PATH", source, StringComparison.Ordinal);
        Assert.Contains("DOTNET_CLI_HOME", source, StringComparison.Ordinal);
        Assert.True(
            buildEnvironmentCall >= 0 &&
            dotnetResolution > buildEnvironmentCall &&
            firstBuild > dotnetResolution &&
            firstPublish > dotnetResolution,
            "D-drive build environment must be fixed before dotnet resolution, build, and publish.");
        Assert.DoesNotContain(
            "\"source_api_base_url=$",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsolatedBuildEnvironment_CreatesAndPropagatesDDriveCachesToChild()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"build-cache-environment-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testRoot, "build-cache-harness.ps1");
        var childPath = Path.Combine(testRoot, "build-cache-child.ps1");
        var environmentPath = Path.Combine(testRoot, "child-environment.txt");
        Directory.CreateDirectory(testRoot);

        try
        {
            File.WriteAllText(
                childPath,
                """
                param([Parameter(Mandatory = $true)][string]$EnvironmentPath)

                [IO.File]::WriteAllLines(
                    $EnvironmentPath,
                    @(
                        $env:TEMP,
                        $env:TMP,
                        $env:NUGET_PACKAGES,
                        $env:NUGET_HTTP_CACHE_PATH,
                        $env:NUGET_PLUGINS_CACHE_PATH,
                        $env:DOTNET_CLI_HOME
                    ),
                    [Text.UTF8Encoding]::new($false))
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$ChildScript,
                    [Parameter(Mandatory = $true)][string]$PowerShellPath,
                    [Parameter(Mandatory = $true)][string]$EnvironmentPath
                )

                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                foreach ($functionName in @(
                    'Get-IsolatedBuildEnvironmentPaths',
                    'Initialize-IsolatedBuildEnvironmentOnD'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                $expected = [ordered]@{
                    TEMP = 'D:\DevCaches\georaeplan-v1-prepare\temp'
                    TMP = 'D:\DevCaches\georaeplan-v1-prepare\temp'
                    NUGET_PACKAGES =
                        'D:\DevCaches\georaeplan-v1-prepare\nuget\packages'
                    NUGET_HTTP_CACHE_PATH =
                        'D:\DevCaches\georaeplan-v1-prepare\nuget\http-cache'
                    NUGET_PLUGINS_CACHE_PATH =
                        'D:\DevCaches\georaeplan-v1-prepare\nuget\plugins-cache'
                    DOTNET_CLI_HOME =
                        'D:\DevCaches\georaeplan-v1-prepare\dotnet-home'
                }

                Initialize-IsolatedBuildEnvironmentOnD `
                    -EnvironmentPaths $expected
                foreach ($entry in $expected.GetEnumerator()) {
                    $actual = [Environment]::GetEnvironmentVariable(
                        $entry.Key,
                        'Process')
                    if (-not [string]::Equals(
                        $actual,
                        $entry.Value,
                        [StringComparison]::OrdinalIgnoreCase)) {
                        throw "$($entry.Key) was not pinned to its D-drive cache."
                    }
                    if (-not (Test-Path -LiteralPath $actual -PathType Container)) {
                        throw "$($entry.Key) cache directory was not created."
                    }
                }

                & $PowerShellPath `
                    -NoProfile `
                    -NonInteractive `
                    -ExecutionPolicy Bypass `
                    -File $ChildScript `
                    -EnvironmentPath $EnvironmentPath
                if ($LASTEXITCODE -ne 0) {
                    throw "Child environment probe failed with exit code $LASTEXITCODE."
                }
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var powerShellPath = ResolveWindowsPowerShellPath();
            var result = await RunPowerShellAsync(
                powerShellPath,
                harnessPath,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolvePreparationScript(),
                "-ChildScript",
                childPath,
                "-PowerShellPath",
                powerShellPath,
                "-EnvironmentPath",
                environmentPath);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Equal(
                new[]
                {
                    @"D:\DevCaches\georaeplan-v1-prepare\temp",
                    @"D:\DevCaches\georaeplan-v1-prepare\temp",
                    @"D:\DevCaches\georaeplan-v1-prepare\nuget\packages",
                    @"D:\DevCaches\georaeplan-v1-prepare\nuget\http-cache",
                    @"D:\DevCaches\georaeplan-v1-prepare\nuget\plugins-cache",
                    @"D:\DevCaches\georaeplan-v1-prepare\dotnet-home"
                },
                File.ReadAllLines(environmentPath));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task StoredCredentialProbeFailure_DoesNotEchoChildOutput()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-redaction-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testRoot, "credential-redaction.ps1");
        var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
        var statusLogPath = Path.Combine(testRoot, "credential-status.log");
        Directory.CreateDirectory(testRoot);

        try
        {
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                echo child-credential-secret
                1>&2 echo child-stderr-secret
                exit /b 19
                """,
                Encoding.ASCII);
            File.WriteAllText(
                harnessPath,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$FakeDotnet,
                    [Parameter(Mandatory = $true)][string]$TestRoot,
                    [Parameter(Mandatory = $true)][string]$StatusLog
                )

                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                foreach ($functionName in @(
                    'New-Utf8NoBomEncoding',
                    'Write-Utf8File',
                    'Invoke-WithProcessEnvironment',
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'Initialize-StoredCredentialBoundedProcessCapture',
                    'Invoke-StoredCredentialEnvelopeProcess',
                    'ConvertFrom-StoredCredentialEnvelopeProcessResult',
                    'Get-StoredSyncCredentialsFromLocalState'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                function Invoke-StoredCredentialEnvelopeProcess {
                    return [pscustomobject]@{
                        ExitCode = 19
                        Stdout = 'child-credential-secret'
                        Stderr = 'child-stderr-secret'
                        FailureReason = 'exit_code'
                        InvocationMode = 'test-fixture'
                    }
                }

                try {
                    Get-StoredSyncCredentialsFromLocalState `
                        -DotnetExe $FakeDotnet `
                        -SyncDiagProject (Join-Path $TestRoot 'fixture.csproj') `
                        -AppRoot $TestRoot `
                        -LogPath $StatusLog |
                        Out-Null
                    throw 'The failing child process was accepted.'
                }
                catch {
                    $message = $_.Exception.Message
                    if (
                        $message.Contains('child-credential-secret') -or
                        $message.Contains('child-stderr-secret')
                    ) {
                        throw 'Credential child output was echoed by the exception.'
                    }
                    if (-not $message.Contains($StatusLog)) {
                        throw 'The credential failure did not use the redacted error.'
                    }
                }

                $status = Get-Content -LiteralPath $StatusLog -Raw
                if (
                    $status -notmatch
                        'stored_credentials_child_output_redacted=True' -or
                    $status.Contains('child-credential-secret') -or
                    $status.Contains('child-stderr-secret')
                ) {
                    throw 'The credential failure status log was not sanitized.'
                }
                Write-Output 'credential_child_output_redacted'
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolvePreparationScript(),
                "-FakeDotnet",
                fakeDotnetPath,
                "-TestRoot",
                testRoot,
                "-StatusLog",
                statusLogPath);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "credential_child_output_redacted",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "child-credential-secret",
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "child-stderr-secret",
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task StoredCredentialArtifactCleanup_DisposesEveryLeaseAcrossFailures()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-cleanup-fault-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testRoot, "harness.ps1");
        Directory.CreateDirectory(testRoot);

        try
        {
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [string]$SourceScript,
                    [string]$WorkParent
                )
                $ErrorActionPreference = 'Stop'
                Set-StrictMode -Version Latest
                $Configuration = 'Debug'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                foreach ($functionName in @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'Initialize-StoredCredentialBoundedProcessCapture',
                    'Remove-StaleSecureIsolatedWorkDirectories',
                    'New-SecureIsolatedWorkDirectory',
                    'Remove-SecureIsolatedWorkDirectory',
                    'Open-StoredCredentialArtifactDirectoryLease',
                    'Close-StoredCredentialArtifactTreeLease',
                    'Invoke-StoredCredentialEnvelopeProcess'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                Add-Type -TypeDefinition @'
                using System;

                public sealed class ThrowAfterDispose : IDisposable
                {
                    private readonly IDisposable inner;
                    private readonly string message;

                    public ThrowAfterDispose(IDisposable inner, string message)
                    {
                        this.inner = inner;
                        this.message = message;
                    }

                    public bool DisposeCalled { get; private set; }

                    public void Dispose()
                    {
                        DisposeCalled = true;
                        inner.Dispose();
                        throw new InvalidOperationException(message);
                    }
                }
                '@

                Initialize-TestEnvironmentFinalPathNativeMethods
                $utf8 = New-Object Text.UTF8Encoding($false)

                $closeRoot = Join-Path $WorkParent 'close-fault'
                $closeChild = Join-Path $closeRoot 'child'
                [IO.Directory]::CreateDirectory($closeChild) | Out-Null
                $closeManifestPath = Join-Path $closeRoot 'manifest.txt'
                $closeFilePath = Join-Path $closeRoot 'artifact.bin'
                [IO.File]::WriteAllText($closeManifestPath, 'manifest', $utf8)
                [IO.File]::WriteAllText($closeFilePath, 'artifact', $utf8)
                $manifestFault = [ThrowAfterDispose]::new(
                    ([IO.FileStream]::new(
                        $closeManifestPath,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Read,
                        [IO.FileShare]::Read)),
                    'forced manifest dispose')
                $fileFault = [ThrowAfterDispose]::new(
                    ([IO.FileStream]::new(
                        $closeFilePath,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Read,
                        [IO.FileShare]::Read)),
                    'forced file dispose')
                $directoryFault = [ThrowAfterDispose]::new(
                    (Open-StoredCredentialArtifactDirectoryLease `
                        -Path $closeChild),
                    'forced directory dispose')
                $closeTree = [pscustomobject]@{
                    Root = $closeRoot
                    Files = @(
                        [pscustomobject]@{ Stream = $fileFault }
                    )
                    DirectoryLeases = @($directoryFault)
                }
                $closeTree | Add-Member `
                    -NotePropertyName ManifestStream `
                    -NotePropertyValue $manifestFault
                $closeFailure = $null
                try {
                    Close-StoredCredentialArtifactTreeLease -Tree $closeTree
                }
                catch {
                    $closeFailure = $_
                }
                if (
                    $null -eq $closeFailure -or
                    $closeFailure.Exception.Message -cnotlike
                        '*forced manifest dispose*' -or
                    -not $manifestFault.DisposeCalled -or
                    -not $fileFault.DisposeCalled -or
                    -not $directoryFault.DisposeCalled
                ) {
                    throw 'Artifact tree close did not preserve its first failure.'
                }
                $renamedCloseRoot = $closeRoot + '.renamed'
                [IO.Directory]::Move($closeRoot, $renamedCloseRoot)
                [IO.Directory]::Delete($renamedCloseRoot, $true)

                $prepCacheRoot = Join-Path $WorkParent 'prep-cache'
                [IO.Directory]::CreateDirectory($prepCacheRoot) | Out-Null
                $prepCacheFault = [ThrowAfterDispose]::new(
                    (Open-StoredCredentialArtifactDirectoryLease `
                        -Path $prepCacheRoot),
                    'forced prep cache dispose')
                $prepWork =
                    New-SecureIsolatedWorkDirectory -Parent $prepCacheRoot
                $prepArtifactRoot = Join-Path $prepWork.Root 'publish'
                [IO.Directory]::CreateDirectory($prepArtifactRoot) | Out-Null
                $prepManifestPath =
                    Join-Path $prepArtifactRoot 'artifact-manifest.txt'
                $prepDllPath = Join-Path $prepArtifactRoot 'SyncDiag.dll'
                [IO.File]::WriteAllText($prepManifestPath, 'manifest', $utf8)
                [IO.File]::WriteAllText($prepDllPath, 'fixture', $utf8)
                $prepManifestFault = [ThrowAfterDispose]::new(
                    ([IO.FileStream]::new(
                        $prepManifestPath,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Read,
                        [IO.FileShare]::Read)),
                    'forced prep manifest dispose')
                $prepFileFault = [ThrowAfterDispose]::new(
                    ([IO.FileStream]::new(
                        $prepDllPath,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Read,
                        [IO.FileShare]::Read)),
                    'forced prep file dispose')
                $prepDirectoryFault = [ThrowAfterDispose]::new(
                    (Open-StoredCredentialArtifactDirectoryLease `
                        -Path $prepArtifactRoot),
                    'forced prep directory dispose')
                $prepTree = [pscustomobject]@{
                    Root = $prepArtifactRoot
                    ManifestContent = 'manifest'
                    Files = @(
                        [pscustomobject]@{ Stream = $prepFileFault }
                    )
                    DirectoryLeases = @($prepDirectoryFault)
                }
                $prepTree | Add-Member `
                    -NotePropertyName ManifestStream `
                    -NotePropertyValue $prepManifestFault
                $script:faultArtifact = [pscustomobject]@{
                    Root = $prepArtifactRoot
                    DllPath = $prepDllPath
                    ManifestPath = $prepManifestPath
                    SourceSha256 = ('A' * 64)
                    ArtifactSha256 = ('B' * 64)
                    TreeLease = $prepTree
                    CacheRootLease = $prepCacheFault
                    WorkDirectory = $prepWork
                }
                function New-StoredCredentialEnvelopeArtifact {
                    return $script:faultArtifact
                }
                function Get-StoredCredentialSourceManifestSha256 {
                    return ('A' * 64)
                }
                function Assert-StoredCredentialArtifactTreeIntegrity {
                    throw 'forced artifact integrity failure'
                }
                $prepResult = Invoke-StoredCredentialEnvelopeProcess `
                    -DotnetExe 'unused.exe' `
                    -SyncDiagProject (
                        Join-Path $WorkParent 'fixture\SyncDiag.csproj')
                if (
                    $prepResult.FailureReason -cne
                        'artifact_preparation_failed' -or
                    -not $prepManifestFault.DisposeCalled -or
                    -not $prepFileFault.DisposeCalled -or
                    -not $prepDirectoryFault.DisposeCalled -or
                    -not $prepCacheFault.DisposeCalled -or
                    $null -ne $prepWork.RootLease -or
                    $null -ne $prepWork.ParentLease -or
                    (Test-Path -LiteralPath $prepWork.Root)
                ) {
                    throw 'Preparation failure leaked an artifact lease.'
                }
                $renamedPrepCache = $prepCacheRoot + '.renamed'
                [IO.Directory]::Move($prepCacheRoot, $renamedPrepCache)
                [IO.Directory]::Delete($renamedPrepCache, $false)

                $processCacheRoot = Join-Path $WorkParent 'process-cache'
                [IO.Directory]::CreateDirectory($processCacheRoot) | Out-Null
                $processCacheFault = [ThrowAfterDispose]::new(
                    (Open-StoredCredentialArtifactDirectoryLease `
                        -Path $processCacheRoot),
                    'forced process cache dispose')
                $processWork =
                    New-SecureIsolatedWorkDirectory -Parent $processCacheRoot
                $processArtifactRoot =
                    Join-Path $processWork.Root 'publish'
                [IO.Directory]::CreateDirectory($processArtifactRoot) |
                    Out-Null
                $processManifestPath =
                    Join-Path $processArtifactRoot 'artifact-manifest.txt'
                $processDllPath =
                    Join-Path $processArtifactRoot 'SyncDiag.dll'
                [IO.File]::WriteAllText(
                    $processManifestPath,
                    'manifest',
                    $utf8)
                [IO.File]::WriteAllText(
                    $processDllPath,
                    'fixture',
                    $utf8)
                $foreignVictim =
                    Join-Path $WorkParent 'foreign-victim.bin'
                [IO.File]::WriteAllText(
                    $foreignVictim,
                    'foreign-victim-content',
                    $utf8)
                $foreignHash = (
                    Get-FileHash `
                        -LiteralPath $foreignVictim `
                        -Algorithm SHA256).Hash
                $hardlink =
                    Join-Path $processWork.Root 'foreign-victim-link.bin'
                New-Item `
                    -ItemType HardLink `
                    -Path $hardlink `
                    -Target $foreignVictim |
                    Out-Null
                $processTree = [pscustomobject]@{
                    Root = $processArtifactRoot
                    ManifestContent = 'manifest'
                    Files = @()
                    DirectoryLeases = @()
                }
                $script:faultArtifact = [pscustomobject]@{
                    Root = $processArtifactRoot
                    DllPath = $processDllPath
                    ManifestPath = $processManifestPath
                    SourceSha256 = ('A' * 64)
                    ArtifactSha256 = ('B' * 64)
                    TreeLease = $processTree
                    CacheRootLease = $processCacheFault
                    WorkDirectory = $processWork
                }
                function Assert-StoredCredentialArtifactTreeIntegrity {
                    return
                }
                $fixtureProject =
                    Join-Path $WorkParent 'fixture\SyncDiag.csproj'
                [IO.Directory]::CreateDirectory(
                    (Split-Path -Parent $fixtureProject)) |
                    Out-Null
                [IO.File]::WriteAllText($fixtureProject, '<Project />', $utf8)
                $processFailure = $null
                try {
                    Invoke-StoredCredentialEnvelopeProcess `
                        -DotnetExe (
                            Join-Path $env:SystemRoot 'System32\cmd.exe') `
                        -SyncDiagProject $fixtureProject |
                        Out-Null
                }
                catch {
                    $processFailure = $_
                }
                if (
                    $null -eq $processFailure -or
                    $processFailure.Exception.Message -ceq
                        'forced process cache dispose' -or
                    -not $processCacheFault.DisposeCalled -or
                    $null -ne $processWork.RootLease -or
                    $null -ne $processWork.ParentLease -or
                    -not (Test-Path -LiteralPath $processWork.Root) -or
                    -not (Test-Path -LiteralPath $foreignVictim) -or
                    (Get-FileHash `
                        -LiteralPath $foreignVictim `
                        -Algorithm SHA256).Hash -cne $foreignHash
                ) {
                    throw 'Process failure did not fail closed safely.'
                }

                [IO.File]::Delete($hardlink)
                $residueRoot = [string]$processWork.Root
                $recoveredWork =
                    New-SecureIsolatedWorkDirectory -Parent $processCacheRoot
                if (Test-Path -LiteralPath $residueRoot) {
                    throw 'Subsequent stale cleanup did not recover residue.'
                }
                Remove-SecureIsolatedWorkDirectory `
                    -WorkDirectory $recoveredWork
                $renamedProcessCache = $processCacheRoot + '.renamed'
                [IO.Directory]::Move(
                    $processCacheRoot,
                    $renamedProcessCache)
                [IO.Directory]::Delete($renamedProcessCache, $false)
                [IO.File]::Delete($foreignVictim)
                [IO.Directory]::Delete(
                    (Split-Path -Parent $fixtureProject),
                    $true)

                Write-Output 'artifact_cleanup_faults_verified'
                """,
                new UTF8Encoding(false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(60),
                "-SourceScript",
                ResolvePreparationScript(),
                "-WorkParent",
                testRoot);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "artifact_cleanup_faults_verified",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Single(
                Directory.EnumerateFiles(
                    testRoot,
                    "*",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateDirectories(
                    testRoot,
                    "*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task StoredCredentialArtifactPreparationCatch_ReleasesRealTreeAndWork()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-real-prep-catch-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testRoot, "harness.ps1");
        var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
        var fakeDotnetScript = Path.Combine(testRoot, "fake-dotnet.ps1");
        var publishRootLog = Path.Combine(testRoot, "publish-root.txt");
        var syncDiagProject = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "SyncDiag.csproj");
        Directory.CreateDirectory(testRoot);

        try
        {
            File.WriteAllText(
                fakeDotnetScript,
                """
                $allArguments = @($args | ForEach-Object { [string]$_ })
                if (
                    $allArguments.Count -eq 0 -or
                    $allArguments[0] -eq 'restore'
                ) {
                    exit 0
                }
                if ($allArguments[0] -cne 'publish') {
                    exit 2
                }
                $outputIndex = [Array]::IndexOf(
                    [string[]]$allArguments,
                    '-o')
                if (
                    $outputIndex -lt 0 -or
                    $outputIndex + 1 -ge $allArguments.Count
                ) {
                    exit 3
                }
                $outputRoot = $allArguments[$outputIndex + 1]
                $nestedRoot = Join-Path $outputRoot 'nested'
                [IO.Directory]::CreateDirectory($nestedRoot) | Out-Null
                [IO.File]::WriteAllText(
                    (Join-Path $outputRoot 'SyncDiag.dll'),
                    'fixture assembly')
                [IO.File]::WriteAllText(
                    (Join-Path $nestedRoot 'payload.bin'),
                    'fixture payload')
                [IO.File]::WriteAllText(
                    $env:FAKE_ARTIFACT_ROOT_LOG,
                    $outputRoot)
                exit 0
                """,
                new UTF8Encoding(true));
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0fake-dotnet.ps1" %*
                exit /b %ERRORLEVEL%
                """,
                Encoding.ASCII);
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [string]$SourceScript,
                    [string]$DotnetExe,
                    [string]$SyncDiagProject,
                    [string]$PublishRootLog
                )
                $ErrorActionPreference = 'Stop'
                Set-StrictMode -Version Latest
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                foreach ($functionName in @(
                    'New-Utf8NoBomEncoding',
                    'Write-Utf8File',
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'Initialize-StoredCredentialBoundedProcessCapture',
                    'Get-StoredCredentialSourceManifestSha256',
                    'Remove-StaleSecureIsolatedWorkDirectories',
                    'New-SecureIsolatedWorkDirectory',
                    'Remove-SecureIsolatedWorkDirectory',
                    'Open-StoredCredentialArtifactDirectoryLease',
                    'Open-StoredCredentialArtifactTreeLease',
                    'Assert-StoredCredentialArtifactTreeIntegrity',
                    'Close-StoredCredentialArtifactTreeLease',
                    'New-StoredCredentialEnvelopeArtifact'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                $cacheRoot =
                    'D:\DevCaches\georaeplan-v1-prepare\stored-credential-envelope'
                [IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
                $beforeRoots = @(
                    [IO.Directory]::EnumerateDirectories(
                        $cacheRoot,
                        '*',
                        [IO.SearchOption]::TopDirectoryOnly) |
                        ForEach-Object { [IO.Path]::GetFileName($_) } |
                        Sort-Object
                )
                $env:FAKE_ARTIFACT_ROOT_LOG = $PublishRootLog
                function Assert-StoredCredentialArtifactTreeIntegrity {
                    throw 'controlled actual preparation failure'
                }

                $preparationFailure = $null
                try {
                    New-StoredCredentialEnvelopeArtifact `
                        -DotnetExe $DotnetExe `
                        -SyncDiagProject $SyncDiagProject `
                        -ConfigurationName 'Debug' |
                        Out-Null
                }
                catch {
                    $preparationFailure = $_
                }
                if (
                    $null -eq $preparationFailure -or
                    $preparationFailure.Exception.Message -cnotlike
                        '*controlled actual preparation failure*' -or
                    -not (Test-Path -LiteralPath $PublishRootLog -PathType Leaf)
                ) {
                    throw 'The real artifact preparation failure was not preserved.'
                }
                $artifactRoot = [IO.File]::ReadAllText($PublishRootLog)
                $workRoot = Split-Path -Parent $artifactRoot
                if (Test-Path -LiteralPath $workRoot) {
                    throw 'The real artifact preparation catch left work residue.'
                }
                $afterRoots = @(
                    [IO.Directory]::EnumerateDirectories(
                        $cacheRoot,
                        '*',
                        [IO.SearchOption]::TopDirectoryOnly) |
                        ForEach-Object { [IO.Path]::GetFileName($_) } |
                        Sort-Object
                )
                if (
                    @(Compare-Object $beforeRoots $afterRoots).Count -ne 0
                ) {
                    throw 'The real artifact preparation catch changed cache roots.'
                }
                $cacheReacquire =
                    Open-StoredCredentialArtifactDirectoryLease `
                        -Path $cacheRoot `
                        -DeleteCapable
                $cacheReacquire.Dispose()
                Write-Output 'real_preparation_catch_verified'
                """,
                new UTF8Encoding(false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(60),
                "-SourceScript",
                ResolvePreparationScript(),
                "-DotnetExe",
                fakeDotnetPath,
                "-SyncDiagProject",
                syncDiagProject,
                "-PublishRootLog",
                publishRootLog);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "real_preparation_catch_verified",
                result.Stdout,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task StoredCredentialProcessFinally_ReleasesNonEmptyFaultingTree()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-real-finally-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testRoot, "harness.ps1");
        Directory.CreateDirectory(testRoot);

        try
        {
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [string]$SourceScript,
                    [string]$WorkParent
                )
                $ErrorActionPreference = 'Stop'
                Set-StrictMode -Version Latest
                $Configuration = 'Debug'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                foreach ($functionName in @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'Initialize-StoredCredentialBoundedProcessCapture',
                    'Remove-StaleSecureIsolatedWorkDirectories',
                    'New-SecureIsolatedWorkDirectory',
                    'Remove-SecureIsolatedWorkDirectory',
                    'Open-StoredCredentialArtifactDirectoryLease',
                    'Close-StoredCredentialArtifactTreeLease',
                    'Invoke-StoredCredentialEnvelopeProcess'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                Add-Type -TypeDefinition @'
                using System;

                public sealed class FinalizerFault : IDisposable
                {
                    private readonly IDisposable inner;
                    private readonly string message;

                    public FinalizerFault(IDisposable inner, string message)
                    {
                        this.inner = inner;
                        this.message = message;
                    }

                    public bool DisposeCalled { get; private set; }

                    public void Dispose()
                    {
                        DisposeCalled = true;
                        inner.Dispose();
                        throw new InvalidOperationException(message);
                    }
                }
                '@

                Initialize-TestEnvironmentFinalPathNativeMethods
                $utf8 = New-Object Text.UTF8Encoding($false)
                $cacheRoot = Join-Path $WorkParent 'normal-finally-cache'
                [IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
                $cacheFault = [FinalizerFault]::new(
                    (Open-StoredCredentialArtifactDirectoryLease `
                        -Path $cacheRoot),
                    'forced normal cache dispose')
                $work = New-SecureIsolatedWorkDirectory -Parent $cacheRoot
                $artifactRoot = Join-Path $work.Root 'publish'
                $nestedRoot = Join-Path $artifactRoot 'nested'
                [IO.Directory]::CreateDirectory($nestedRoot) | Out-Null
                $manifestPath =
                    Join-Path $artifactRoot 'artifact-manifest.txt'
                $dllPath = Join-Path $artifactRoot 'SyncDiag.dll'
                $payloadPath = Join-Path $nestedRoot 'payload.bin'
                [IO.File]::WriteAllText($manifestPath, 'manifest', $utf8)
                [IO.File]::WriteAllText($dllPath, 'fixture', $utf8)
                [IO.File]::WriteAllText($payloadPath, 'payload', $utf8)
                $manifestFault = [FinalizerFault]::new(
                    ([IO.FileStream]::new(
                        $manifestPath,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Read,
                        [IO.FileShare]::Read)),
                    'forced normal manifest dispose')
                $fileFault = [FinalizerFault]::new(
                    ([IO.FileStream]::new(
                        $payloadPath,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Read,
                        [IO.FileShare]::Read)),
                    'forced normal file dispose')
                $directoryFault = [FinalizerFault]::new(
                    (Open-StoredCredentialArtifactDirectoryLease `
                        -Path $nestedRoot),
                    'forced normal directory dispose')
                $tree = [pscustomobject]@{
                    Root = $artifactRoot
                    ManifestContent = 'manifest'
                    Files = @(
                        [pscustomobject]@{ Stream = $fileFault }
                    )
                    DirectoryLeases = @($directoryFault)
                }
                $tree | Add-Member `
                    -NotePropertyName ManifestStream `
                    -NotePropertyValue $manifestFault
                $script:normalArtifact = [pscustomobject]@{
                    Root = $artifactRoot
                    DllPath = $dllPath
                    ManifestPath = $manifestPath
                    SourceSha256 = ('A' * 64)
                    ArtifactSha256 = ('B' * 64)
                    TreeLease = $tree
                    CacheRootLease = $cacheFault
                    WorkDirectory = $work
                }
                function New-StoredCredentialEnvelopeArtifact {
                    return $script:normalArtifact
                }
                function Get-StoredCredentialSourceManifestSha256 {
                    return ('A' * 64)
                }
                function Assert-StoredCredentialArtifactTreeIntegrity {
                    return
                }
                $fixtureProject =
                    Join-Path $WorkParent 'fixture\SyncDiag.csproj'
                [IO.Directory]::CreateDirectory(
                    (Split-Path -Parent $fixtureProject)) |
                    Out-Null
                [IO.File]::WriteAllText($fixtureProject, '<Project />', $utf8)

                $finallyFailure = $null
                try {
                    Invoke-StoredCredentialEnvelopeProcess `
                        -DotnetExe (
                            Join-Path $env:SystemRoot 'System32\cmd.exe') `
                        -SyncDiagProject $fixtureProject |
                        Out-Null
                }
                catch {
                    $finallyFailure = $_
                }
                if (
                    $null -eq $finallyFailure -or
                    $finallyFailure.Exception.Message -cnotlike
                        '*forced normal manifest dispose*' -or
                    -not $manifestFault.DisposeCalled -or
                    -not $fileFault.DisposeCalled -or
                    -not $directoryFault.DisposeCalled -or
                    -not $cacheFault.DisposeCalled -or
                    $null -ne $work.RootLease -or
                    $null -ne $work.ParentLease -or
                    (Test-Path -LiteralPath $work.Root)
                ) {
                    throw 'The normal process finally leaked a lease.'
                }
                $cacheReacquire =
                    Open-StoredCredentialArtifactDirectoryLease `
                        -Path $cacheRoot `
                        -DeleteCapable
                $cacheReacquire.Dispose()
                $recovered =
                    New-SecureIsolatedWorkDirectory -Parent $cacheRoot
                Remove-SecureIsolatedWorkDirectory -WorkDirectory $recovered
                [IO.Directory]::Delete($cacheRoot, $false)
                [IO.Directory]::Delete(
                    (Split-Path -Parent $fixtureProject),
                    $true)
                Write-Output 'normal_process_finally_verified'
                """,
                new UTF8Encoding(false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(60),
                "-SourceScript",
                ResolvePreparationScript(),
                "-WorkParent",
                testRoot);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "normal_process_finally_verified",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Single(
                Directory.EnumerateFiles(
                    testRoot,
                    "*",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateDirectories(
                    testRoot,
                    "*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Theory]
    [InlineData("malformed-json")]
    [InlineData("invalid-shape")]
    [InlineData("wrong-types")]
    [InlineData("invalid-timestamp")]
    public async Task StoredCredentialProbeInvalidSuccessOutput_DoesNotEchoChildOutput(
        string invalidOutputCase)
    {
        const string secret = "child-credential-parse-secret";
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-parse-redaction-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(
            testRoot,
            "credential-parse-redaction.ps1");
        var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
        var statusLogPath = Path.Combine(
            testRoot,
            "credential-parse-status.log");
        Directory.CreateDirectory(testRoot);

        try
        {
            var childOutput = invalidOutputCase switch
            {
                "malformed-json" =>
                    $"[{{\"Username\":\"{secret}\"",
                "invalid-shape" =>
                    $"[{{\"Username\":\"{secret}\",\"Unexpected\":\"field\"}}]",
                "wrong-types" =>
                    "[{\"OfficeCode\":1,\"TenantCode\":\"TENANT\"," +
                    $"\"Username\":\"{secret}\",\"Password\":1234," +
                    "\"SavedAtUtc\":\"2026-07-29T00:00:00.0000000Z\"}]",
                "invalid-timestamp" =>
                    "[{\"OfficeCode\":\"OFFICE\",\"TenantCode\":\"TENANT\"," +
                    $"\"Username\":\"{secret}\",\"Password\":\"secret\"," +
                    $"\"SavedAtUtc\":\"{secret}-not-a-date\"}}]",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(invalidOutputCase),
                    invalidOutputCase,
                    "Unknown invalid credential output case.")
            };
            File.WriteAllText(
                fakeDotnetPath,
                "@echo off" + Environment.NewLine +
                "echo " + childOutput + Environment.NewLine +
                "exit /b 0" + Environment.NewLine,
                Encoding.ASCII);
            File.WriteAllText(
                harnessPath,
                $$"""
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$FakeDotnet,
                    [Parameter(Mandatory = $true)][string]$TestRoot,
                    [Parameter(Mandatory = $true)][string]$StatusLog
                )

                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                foreach ($functionName in @(
                    'New-Utf8NoBomEncoding',
                    'Write-Utf8File',
                    'Invoke-WithProcessEnvironment',
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'Initialize-StoredCredentialBoundedProcessCapture',
                    'Invoke-StoredCredentialEnvelopeProcess',
                    'ConvertFrom-StoredCredentialEnvelopeProcessResult',
                    'Get-StoredSyncCredentialsFromLocalState'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                $script:invalidEnvelopeOutput =
                    [Text.Encoding]::UTF8.GetString(
                        [Convert]::FromBase64String(
                            '{{Convert.ToBase64String(Encoding.UTF8.GetBytes(childOutput))}}'))
                function Invoke-StoredCredentialEnvelopeProcess {
                    return [pscustomobject]@{
                        ExitCode = 0
                        Stdout = $script:invalidEnvelopeOutput
                        Stderr = ''
                        FailureReason = ''
                        InvocationMode = 'test-fixture'
                    }
                }

                try {
                    Get-StoredSyncCredentialsFromLocalState `
                        -DotnetExe $FakeDotnet `
                        -SyncDiagProject (Join-Path $TestRoot 'fixture.csproj') `
                        -AppRoot $TestRoot `
                        -LogPath $StatusLog |
                        Out-Null
                    throw 'Invalid credential JSON output was accepted.'
                }
                catch {
                    $message = $_.Exception.Message
                    if ($message.Contains('{{secret}}')) {
                        throw 'Invalid credential child output leaked.'
                    }
                    if (-not $message.Contains($StatusLog)) {
                        throw 'The invalid credential error omitted the status log.'
                    }
                }

                $status = Get-Content -LiteralPath $StatusLog -Raw
                if (
                    $status -notmatch
                        'stored_credentials_error=invalid-envelope-or-decryption' -or
                    $status -notmatch
                        'stored_credentials_child_output_redacted=True' -or
                    $status.Contains('{{secret}}')
                ) {
                    throw 'The invalid credential status log was not sanitized.'
                }
                Write-Output 'credential_parse_output_redacted'
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolvePreparationScript(),
                "-FakeDotnet",
                fakeDotnetPath,
                "-TestRoot",
                testRoot,
                "-StatusLog",
                statusLogPath);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "credential_parse_output_redacted",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                secret,
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public void StoredCredentialEnvelopeParser_RejectsDuplicatePropertiesBeforeJsonConversion()
    {
        var preparationSource =
            File.ReadAllText(ResolvePreparationScript());
        var parserStart = preparationSource.IndexOf(
            "function ConvertFrom-StoredCredentialEnvelopeProcessResult",
            StringComparison.Ordinal);
        var parserEnd = preparationSource.IndexOf(
            "function Get-StoredSyncCredentialsFromLocalState",
            parserStart,
            StringComparison.Ordinal);
        Assert.True(parserStart >= 0 && parserEnd > parserStart);

        var parserSource = preparationSource[parserStart..parserEnd];
        var duplicateValidationIndex = parserSource.IndexOf(
            "AssertNoDuplicateJsonObjectPropertiesAndDepth($jsonText, 12)",
            StringComparison.Ordinal);
        var convertFromJsonIndex = parserSource.IndexOf(
            "$jsonText | ConvertFrom-Json",
            StringComparison.Ordinal);
        Assert.True(
            duplicateValidationIndex >= 0 &&
            convertFromJsonIndex > duplicateValidationIndex,
            "Stored credential JSON must reject duplicate and case-variant " +
            "properties before PowerShell materializes the object.");
        Assert.Contains(
            "StringComparer.OrdinalIgnoreCase",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "requiredEnvelopeFields",
            parserSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "requiredCredentialFields",
            parserSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StoredCredentialEnvelopeProcess_IsTimeoutAndOutputBounded()
    {
        var preparationSource =
            File.ReadAllText(ResolvePreparationScript());
        var processStart = preparationSource.IndexOf(
            "function Invoke-StoredCredentialEnvelopeProcess",
            StringComparison.Ordinal);
        var processEnd = preparationSource.IndexOf(
            "function ConvertFrom-StoredCredentialEnvelopeProcessResult",
            processStart,
            StringComparison.Ordinal);
        Assert.True(processStart >= 0 && processEnd > processStart);
        var processSource = preparationSource[processStart..processEnd];

        Assert.Contains(
            "Initialize-StoredCredentialBoundedProcessCapture",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "BoundedProcessCapture]::Run(",
            processSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$TimeoutMilliseconds = 30000",
            processSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$MaximumStdoutBytes = 393216",
            processSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$MaximumStderrBytes = 8192",
            processSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadToEndAsync",
            processSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TerminateJobObject",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssignProcessToJobObject",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateProcessW(",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateSuspended |",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateNoWindow |",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResumeThread(processInformation.Thread)",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProcThreadAttributeHandleList",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateProcThreadAttribute(",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TerminateProcess(processInformation.Process, 254)",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreatePrivateDirectory($rootPath)",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssertPrivateDirectoryAcl($rootPath)",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssertPrivateTreeAcl($workDirectory.Root)",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "New-SecureIsolatedWorkDirectory -Parent $cacheRoot",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "'--force'",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:UseArtifactsOutput=true",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:SkipCopyUpdaterOutput=true",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:ArtifactsPath=$buildArtifactsRoot",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-SecureIsolatedWorkDirectory",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaximumPrivateTreeEntries = 8192",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$maximumArtifactFileCount = 2048",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeleteExactPrivateTreeFile",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ParentLease = $parentLease",
            preparationSource,
            StringComparison.Ordinal);
        var desktopProjectSource = File.ReadAllText(
            Directory.EnumerateFiles(
                Path.Combine(FindRepositoryRoot(), "Desktop"),
                "*.Desktop.App.csproj",
                SearchOption.AllDirectories).Single());
        Assert.Contains(
            "Condition=\"'$(SkipCopyUpdaterOutput)' != 'true'\"",
            desktopProjectSource,
            StringComparison.Ordinal);
        var createProcessIndex = preparationSource.IndexOf(
            "CreateProcessW(",
            StringComparison.Ordinal);
        var assignJobIndex = preparationSource.IndexOf(
            "AssignProcessToJobObject(",
            createProcessIndex,
            StringComparison.Ordinal);
        var resumeThreadIndex = preparationSource.IndexOf(
            "ResumeThread(processInformation.Thread)",
            assignJobIndex,
            StringComparison.Ordinal);
        Assert.True(
            createProcessIndex >= 0 &&
            assignJobIndex > createProcessIndex &&
            resumeThreadIndex > assignJobIndex,
            "The child must join the Job Object before its primary thread resumes.");

        var parserStart = processEnd;
        var parserEnd = preparationSource.IndexOf(
            "function Get-StoredSyncCredentialsFromLocalState",
            parserStart,
            StringComparison.Ordinal);
        var parserSource =
            preparationSource[parserStart..parserEnd];
        Assert.Contains(
            "$jsonText.Length -gt 393216",
            parserSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$credentials.Count -gt 16",
            parserSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssertNoDuplicateJsonObjectPropertiesAndDepth($jsonText, 12)",
            parserSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredCredentialEnvelopeProcess_RejectsStaleBinBeforeCredentialChild()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-stale-bin-{Guid.NewGuid():N}");
        var projectDirectory =
            Path.Combine(testRoot, "tools", "SyncDiag");
        var projectPath =
            Path.Combine(projectDirectory, "fixture.csproj");
        var staleDll = Path.Combine(
            projectDirectory,
            "bin",
            "Debug",
            "net8.0-windows",
            "SyncDiag.dll");
        var fakeDotnet = Path.Combine(testRoot, "fake-dotnet.cmd");
        var argumentLog = Path.Combine(testRoot, "dotnet-arguments.log");
        var harness = Path.Combine(testRoot, "harness.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(staleDll)!);
        Directory.CreateDirectory(Path.Combine(testRoot, "Shared"));

        try
        {
            File.WriteAllText(projectPath, "<Project />", Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(projectDirectory, "Program.cs"),
                "internal static class Fixture {}",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(testRoot, "Shared", "Shared.cs"),
                "internal static class SharedFixture {}",
                Encoding.UTF8);
            File.WriteAllText(
                staleDll,
                "stale-bin-credential-secret",
                Encoding.UTF8);
            File.WriteAllText(
                fakeDotnet,
                "@echo off" + Environment.NewLine +
                "echo %*>>\"" + argumentLog + "\"" + Environment.NewLine +
                "exit /b 0" + Environment.NewLine,
                Encoding.ASCII);
            File.WriteAllText(
                harness,
                """
                param(
                    [string]$SourceScript,
                    [string]$FakeDotnet,
                    [string]$ProjectPath
                )
                $ErrorActionPreference = 'Stop'
                $script:Configuration = 'Debug'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                foreach ($functionName in @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'Initialize-StoredCredentialBoundedProcessCapture',
                    'Get-StoredCredentialSourceManifestSha256',
                    'Open-StoredCredentialArtifactDirectoryLease',
                    'New-StoredCredentialEnvelopeArtifact',
                    'Invoke-StoredCredentialEnvelopeProcess'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $functionName
                    }, $true)
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }
                $result = Invoke-StoredCredentialEnvelopeProcess `
                    -DotnetExe $FakeDotnet `
                    -SyncDiagProject $ProjectPath
                if (
                    [string]$result.FailureReason -cne
                        'artifact_preparation_failed' -or
                    -not [string]::IsNullOrEmpty([string]$result.Stdout) -or
                    -not [string]::IsNullOrEmpty([string]$result.Stderr)
                ) {
                    throw 'The stale artifact was not rejected safely.'
                }
                Write-Output 'stale_bin_rejected_before_child'
                """,
                new UTF8Encoding(false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harness,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolvePreparationScript(),
                "-FakeDotnet",
                fakeDotnet,
                "-ProjectPath",
                projectPath);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "stale_bin_rejected_before_child",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "stale-bin-credential-secret",
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
            var preparationSource =
                File.ReadAllText(ResolvePreparationScript());
            var invokeStart = preparationSource.IndexOf(
                "function Invoke-StoredCredentialEnvelopeProcess",
                StringComparison.Ordinal);
            var invokeEnd = preparationSource.IndexOf(
                "function ConvertFrom-StoredCredentialEnvelopeProcessResult",
                invokeStart,
                StringComparison.Ordinal);
            var invokeSource =
                preparationSource[invokeStart..invokeEnd];
            Assert.True(
                invokeSource.IndexOf(
                    "New-StoredCredentialEnvelopeArtifact",
                    StringComparison.Ordinal) <
                invokeSource.IndexOf(
                    "'stored-credential-envelopes'",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                "bin\\",
                invokeSource,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "'--no-build'",
                invokeSource,
                StringComparison.Ordinal);
            if (File.Exists(argumentLog))
            {
                var invokedArguments = File.ReadAllText(argumentLog);
                Assert.DoesNotContain(
                    "stored-credential-envelopes",
                    invokedArguments,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    staleDll,
                    invokedArguments,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task StoredCredentialArtifactTree_LeasesFilesAndRejectsSetChanges()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-artifact-tree-{Guid.NewGuid():N}");
        var nestedRoot = Path.Combine(testRoot, "nested");
        var artifactPath = Path.Combine(testRoot, "SyncDiag.dll");
        var nestedArtifactPath = Path.Combine(nestedRoot, "dependency.dll");
        var harnessPath = Path.Combine(testRoot, "harness.ps1");
        Directory.CreateDirectory(nestedRoot);

        try
        {
            File.WriteAllText(artifactPath, "pinned-artifact", Encoding.UTF8);
            File.WriteAllText(
                nestedArtifactPath,
                "pinned-dependency",
                Encoding.UTF8);
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [string]$SourceScript,
                    [string]$ArtifactRoot
                )
                $ErrorActionPreference = 'Stop'
                Set-StrictMode -Version Latest
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                foreach ($functionName in @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'Open-StoredCredentialArtifactDirectoryLease',
                    'Open-StoredCredentialArtifactTreeLease',
                    'Assert-StoredCredentialArtifactTreeIntegrity',
                    'Close-StoredCredentialArtifactTreeLease'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                $tree = $null
                try {
                    $tree = Open-StoredCredentialArtifactTreeLease `
                        -Root $ArtifactRoot
                    Assert-StoredCredentialArtifactTreeIntegrity -Tree $tree

                    $writeBlocked = $false
                    try {
                        [IO.File]::WriteAllText(
                            (Join-Path $ArtifactRoot 'SyncDiag.dll'),
                            'tampered')
                    }
                    catch {
                        $writeBlocked = $true
                    }
                    if (-not $writeBlocked) {
                        throw 'A leased artifact file remained writable.'
                    }

                    $deleteBlocked = $false
                    try {
                        [IO.File]::Delete(
                            (Join-Path $ArtifactRoot 'nested\dependency.dll'))
                    }
                    catch {
                        $deleteBlocked = $true
                    }
                    if (-not $deleteBlocked) {
                        throw 'A leased artifact file remained deletable.'
                    }

                    [IO.File]::WriteAllText(
                        (Join-Path $ArtifactRoot 'nested\artifact-manifest.txt'),
                        'unexpected')
                    $setChangeRejected = $false
                    try {
                        Assert-StoredCredentialArtifactTreeIntegrity -Tree $tree
                    }
                    catch {
                        $setChangeRejected = $true
                    }
                    if (-not $setChangeRejected) {
                        throw 'An unexpected nested manifest-named file was ignored.'
                    }
                    Write-Output 'artifact_tree_lease_verified'
                }
                finally {
                    if ($null -ne $tree) {
                        Close-StoredCredentialArtifactTreeLease -Tree $tree
                    }
                }
                """,
                new UTF8Encoding(false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolvePreparationScript(),
                "-ArtifactRoot",
                testRoot);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "artifact_tree_lease_verified",
                result.Stdout,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task SecureIsolatedWorkDirectory_UsesPrivateAclAndLeavesNoResidue()
    {
        var testParent = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"secure-isolated-work-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testParent, "harness.ps1");
        Directory.CreateDirectory(testParent);

        try
        {
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [string]$SourceScript,
                    [string]$WorkParent
                )
                $ErrorActionPreference = 'Stop'
                Set-StrictMode -Version Latest
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                foreach ($functionName in @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'Open-StoredCredentialArtifactDirectoryLease',
                    'Open-StoredCredentialArtifactTreeLease',
                    'Assert-StoredCredentialArtifactTreeIntegrity',
                    'Close-StoredCredentialArtifactTreeLease',
                    'Remove-StaleSecureIsolatedWorkDirectories',
                    'New-SecureIsolatedWorkDirectory',
                    'Remove-SecureIsolatedWorkDirectory'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                Initialize-TestEnvironmentFinalPathNativeMethods
                $forcedFailureParent =
                    Join-Path $WorkParent 'forced-initialization-parent'
                [IO.Directory]::CreateDirectory($forcedFailureParent) |
                    Out-Null
                $originalStaleRemoval = (
                    Get-Command Remove-StaleSecureIsolatedWorkDirectories
                ).ScriptBlock
                Set-Item `
                    -LiteralPath Function:\Remove-StaleSecureIsolatedWorkDirectories `
                    -Value {
                        throw 'forced secure work initialization failure'
                    }
                $forcedInitializationRejected = $false
                try {
                    New-SecureIsolatedWorkDirectory `
                        -Parent $forcedFailureParent |
                        Out-Null
                }
                catch {
                    $forcedInitializationRejected = $true
                }
                finally {
                    Set-Item `
                        -LiteralPath Function:\Remove-StaleSecureIsolatedWorkDirectories `
                        -Value $originalStaleRemoval
                }
                $forcedFailureRenamed = $forcedFailureParent + '.renamed'
                [IO.Directory]::Move(
                    $forcedFailureParent,
                    $forcedFailureRenamed)
                [IO.Directory]::Move(
                    $forcedFailureRenamed,
                    $forcedFailureParent)
                if (-not $forcedInitializationRejected) {
                    throw 'Forced secure work initialization did not fail.'
                }
                [IO.Directory]::Delete($forcedFailureParent, $false)

                $broadParent = Join-Path $WorkParent 'broad-parent'
                [IO.Directory]::CreateDirectory($broadParent) | Out-Null
                for ($index = 0; $index -lt 1024; $index++) {
                    [IO.Directory]::CreateDirectory(
                        (Join-Path $broadParent ("foreign-{0:D4}" -f $index))) |
                        Out-Null
                }
                $broadScan = [Diagnostics.Stopwatch]::StartNew()
                $boundedWork =
                    New-SecureIsolatedWorkDirectory -Parent $broadParent
                $broadScan.Stop()
                if (
                    $broadScan.Elapsed -gt [TimeSpan]::FromSeconds(5) -or
                    @(
                        [IO.Directory]::EnumerateDirectories(
                            $broadParent,
                            'foreign-*',
                            [IO.SearchOption]::TopDirectoryOnly)
                    ).Count -ne 1024
                ) {
                    throw 'Raw secure-work child enumeration was not bounded.'
                }
                Remove-SecureIsolatedWorkDirectory `
                    -WorkDirectory $boundedWork
                [IO.Directory]::Delete($broadParent, $true)

                $foreignRoot = Join-Path `
                    $WorkParent `
                    ([Guid]::NewGuid().ToString('N'))
                [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                    CreatePrivateDirectory($foreignRoot)
                $outsideHardlinkTarget =
                    Join-Path $foreignRoot 'outside-hardlink-target.bin'
                [IO.File]::WriteAllText(
                    $outsideHardlinkTarget,
                    'outside-hardlink-content')
                $stale = New-SecureIsolatedWorkDirectory -Parent $WorkParent
                $staleRoot = [string]$stale.Root
                $stale.RootLease.Dispose()
                $stale.RootLease = $null
                $stale.ParentLease.Dispose()
                $stale.ParentLease = $null

                $work = New-SecureIsolatedWorkDirectory -Parent $WorkParent
                if (
                    (Test-Path -LiteralPath $staleRoot) -or
                    -not (Test-Path -LiteralPath $foreignRoot)
                ) {
                    throw 'Secure residue scavenging crossed its ownership marker.'
                }
                $root = [string]$work.Root
                try {
                    [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                        AssertPrivateDirectoryAcl($root)
                    $parentRenameBlocked = $false
                    try {
                        [IO.Directory]::Move(
                            $WorkParent,
                            $WorkParent + '.swapped')
                    }
                    catch {
                        $parentRenameBlocked = $true
                    }
                    if (
                        -not $parentRenameBlocked -or
                        $null -eq $work.ParentLease
                    ) {
                        throw 'The weak parent was not pinned against swapping.'
                    }
                    $renameBlocked = $false
                    try {
                        [IO.Directory]::Move($root, $root + '.swapped')
                    }
                    catch {
                        $renameBlocked = $true
                    }
                    if (-not $renameBlocked) {
                        throw 'The pinned private root could be swapped.'
                    }
                    $deleteBlocked = $false
                    try {
                        [IO.Directory]::Delete($root, $false)
                    }
                    catch {
                        $deleteBlocked = $true
                    }
                    if (-not $deleteBlocked) {
                        throw 'The pinned private root could be replaced.'
                    }
                    $nested = Join-Path $root 'nested'
                    [IO.Directory]::CreateDirectory($nested) | Out-Null
                    [IO.File]::WriteAllText(
                        (Join-Path $nested 'fixture.bin'),
                        'private-fixture')
                    [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                        AssertPrivateTreeAcl($root)
                    $outsideHashBefore = (
                        Get-FileHash `
                            -LiteralPath $outsideHardlinkTarget `
                            -Algorithm SHA256).Hash
                    $outsideAttributesBefore = (
                        Get-Item -LiteralPath $outsideHardlinkTarget
                    ).Attributes
                    $hardlink = Join-Path $root 'outside-hardlink.bin'
                    New-Item `
                        -ItemType HardLink `
                        -Path $hardlink `
                        -Target $outsideHardlinkTarget |
                        Out-Null
                    $hardlinkRejected = $false
                    try {
                        Remove-SecureIsolatedWorkDirectory `
                            -WorkDirectory $work
                    }
                    catch {
                        $hardlinkRejected = $true
                    }
                    $outsideHashAfter = (
                        Get-FileHash `
                            -LiteralPath $outsideHardlinkTarget `
                            -Algorithm SHA256).Hash
                    $outsideAttributesAfter = (
                        Get-Item -LiteralPath $outsideHardlinkTarget
                    ).Attributes
                    if (
                        -not $hardlinkRejected -or
                        -not (Test-Path -LiteralPath $root) -or
                        -not (Test-Path `
                            -LiteralPath (
                                Join-Path `
                                    $root `
                                    '.georaeplan-secure-work-v1') `
                            -PathType Leaf) -or
                        $null -ne $work.RootLease -or
                        $null -ne $work.ParentLease -or
                        $outsideHashAfter -cne $outsideHashBefore -or
                        $outsideAttributesAfter -ne $outsideAttributesBefore
                    ) {
                        throw 'A private-tree hardlink was not rejected safely.'
                    }
                    $releasedRootLease =
                        Open-StoredCredentialArtifactDirectoryLease `
                            -Path $root `
                            -DeleteCapable
                    $releasedRootLease.Dispose()
                    [IO.File]::Delete($hardlink)
                    if (
                        (Get-FileHash `
                            -LiteralPath $outsideHardlinkTarget `
                            -Algorithm SHA256).Hash -cne $outsideHashBefore
                    ) {
                        throw 'Removing the test hardlink changed its target.'
                    }
                    $recoveredRoot = $root
                    $work =
                        New-SecureIsolatedWorkDirectory -Parent $WorkParent
                    $root = [string]$work.Root
                    if (Test-Path -LiteralPath $recoveredRoot) {
                        throw 'Valid hardlink residue was not scavenged.'
                    }
                    $outside = Join-Path $WorkParent 'outside-target'
                    [IO.Directory]::CreateDirectory($outside) | Out-Null
                    $junction = Join-Path $root 'untrusted-junction'
                    New-Item `
                        -ItemType Junction `
                        -Path $junction `
                        -Target $outside |
                        Out-Null
                    $reparseRejected = $false
                    try {
                        Remove-SecureIsolatedWorkDirectory `
                            -WorkDirectory $work
                    }
                    catch {
                        $reparseRejected = $true
                    }
                    if (
                        -not $reparseRejected -or
                        -not (Test-Path -LiteralPath $root) -or
                        $null -ne $work.RootLease -or
                        $null -ne $work.ParentLease
                    ) {
                        throw 'Unsafe reparse cleanup did not fail closed.'
                    }
                    $releasedRootLease =
                        Open-StoredCredentialArtifactDirectoryLease `
                            -Path $root `
                            -DeleteCapable
                    $releasedRootLease.Dispose()
                    [IO.Directory]::Delete($junction, $false)
                    $recoveredRoot = $root
                    $work =
                        New-SecureIsolatedWorkDirectory -Parent $WorkParent
                    $root = [string]$work.Root
                    if (Test-Path -LiteralPath $recoveredRoot) {
                        throw 'Valid reparse residue was not scavenged.'
                    }
                }
                finally {
                    if ($null -ne $work.RootLease) {
                        Remove-SecureIsolatedWorkDirectory `
                            -WorkDirectory $work
                    }
                    $outside = Join-Path $WorkParent 'outside-target'
                    if (Test-Path -LiteralPath $outside) {
                        [IO.Directory]::Delete($outside, $false)
                    }
                    if (Test-Path -LiteralPath $foreignRoot) {
                        $foreignLease =
                            Open-StoredCredentialArtifactDirectoryLease `
                                -Path $foreignRoot `
                                -DeleteCapable
                        try {
                            [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                                DeletePrivateTreeAndRoot(
                                    $foreignLease,
                                    $foreignRoot)
                        }
                        finally {
                            $foreignLease.Dispose()
                        }
                    }
                }
                if (Test-Path -LiteralPath $root) {
                    throw 'The private GUID work directory left residue.'
                }
                Write-Output 'secure_work_directory_verified'
                """,
                new UTF8Encoding(false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolvePreparationScript(),
                "-WorkParent",
                testParent);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "secure_work_directory_verified",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Single(
                Directory.EnumerateFiles(
                    testParent,
                    "*",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateDirectories(
                    testParent,
                    "*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testParent);
        }
    }

    [Fact]
    public async Task StoredCredentialEnvelopeArtifact_PublishesFreshAndLeavesNoResidue()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-fresh-publish-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testRoot, "harness.ps1");
        var repositoryRoot = FindRepositoryRoot();
        var syncDiagProject = Path.Combine(
            repositoryRoot,
            "tools",
            "SyncDiag",
            "SyncDiag.csproj");
        var updaterProject = Assert.Single(
            Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, "Updater"),
                "*.Updater.csproj",
                SearchOption.AllDirectories));
        var staleUpdaterMarkerName =
            $".stale-updater-marker-{Guid.NewGuid():N}.txt";
        var staleUpdaterMarkerPath = Path.Combine(
            Path.GetDirectoryName(updaterProject)!,
            "bin",
            "Debug",
            "net8.0-windows",
            staleUpdaterMarkerName);
        var dotnetExe =
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Assert.False(string.IsNullOrWhiteSpace(dotnetExe));
        Assert.True(File.Exists(dotnetExe));
        Directory.CreateDirectory(testRoot);

        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(staleUpdaterMarkerPath)!);
            File.WriteAllText(
                staleUpdaterMarkerPath,
                "stale-updater-output",
                new UTF8Encoding(false));
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [string]$SourceScript,
                    [string]$DotnetExe,
                    [string]$SyncDiagProject,
                    [string]$StaleUpdaterMarkerName
                )
                $ErrorActionPreference = 'Stop'
                Set-StrictMode -Version Latest
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                foreach ($functionName in @(
                    'New-Utf8NoBomEncoding',
                    'Write-Utf8File',
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'Initialize-StoredCredentialBoundedProcessCapture',
                    'Get-StoredCredentialSourceManifestSha256',
                    'Remove-StaleSecureIsolatedWorkDirectories',
                    'New-SecureIsolatedWorkDirectory',
                    'Remove-SecureIsolatedWorkDirectory',
                    'Open-StoredCredentialArtifactDirectoryLease',
                    'Open-StoredCredentialArtifactTreeLease',
                    'Assert-StoredCredentialArtifactTreeIntegrity',
                    'Close-StoredCredentialArtifactTreeLease',
                    'New-StoredCredentialEnvelopeArtifact'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                $artifact = $null
                $workRoot = ''
                try {
                    $artifact = New-StoredCredentialEnvelopeArtifact `
                        -DotnetExe $DotnetExe `
                        -SyncDiagProject $SyncDiagProject `
                        -ConfigurationName 'Debug'
                    $workRoot = [string]$artifact.WorkDirectory.Root
                    if (
                        -not (Test-Path -LiteralPath $artifact.DllPath -PathType Leaf) -or
                        -not (Test-Path -LiteralPath $artifact.ManifestPath -PathType Leaf)
                    ) {
                        throw 'The fresh pinned artifact is incomplete.'
                    }
                    [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
                        AssertPrivateTreeAcl($workRoot)
                    if (
                        @(
                            Get-ChildItem `
                                -LiteralPath $artifact.Root `
                                -Recurse `
                                -File `
                                -Filter $StaleUpdaterMarkerName
                        ).Count -ne 0
                    ) {
                        throw 'A stale repository Updater/bin file entered the artifact.'
                    }
                }
                finally {
                    if ($null -ne $artifact) {
                        Close-StoredCredentialArtifactTreeLease `
                            -Tree $artifact.TreeLease
                        $artifact.CacheRootLease.Dispose()
                        Remove-SecureIsolatedWorkDirectory `
                            -WorkDirectory $artifact.WorkDirectory
                    }
                }
                if (
                    [string]::IsNullOrWhiteSpace($workRoot) -or
                    (Test-Path -LiteralPath $workRoot)
                ) {
                    throw 'The successful fresh publish left GUID residue.'
                }
                Write-Output 'fresh_publish_cleanup_verified'
                """,
                new UTF8Encoding(false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromMinutes(4),
                "-SourceScript",
                ResolvePreparationScript(),
                "-DotnetExe",
                dotnetExe!,
                "-SyncDiagProject",
                syncDiagProject,
                "-StaleUpdaterMarkerName",
                staleUpdaterMarkerName);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "fresh_publish_cleanup_verified",
                result.Stdout,
                StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(staleUpdaterMarkerPath))
            {
                File.Delete(staleUpdaterMarkerPath);
            }
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("stdout_limit")]
    [InlineData("descendant_stress")]
    public async Task StoredCredentialEnvelopeProcess_KillsOwnedTreeAndRedactsFailure(
        string failureMode)
    {
        const string secret = "bounded-child-secret";
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-bounded-{failureMode}-{Guid.NewGuid():N}");
        var fakeDotnet = Path.Combine(testRoot, "fake-dotnet.cmd");
        var childPidPath = Path.Combine(testRoot, "child.pid");
        var harnessPath = Path.Combine(testRoot, "harness.ps1");
        Directory.CreateDirectory(testRoot);

        try
        {
            var escapedPidPath = childPidPath.Replace("'", "''");
            var nestedSleep = Convert.ToBase64String(
                Encoding.Unicode.GetBytes("Start-Sleep -Seconds 60"));
            var childCommand = failureMode switch
            {
                "timeout" =>
                    $"[IO.File]::WriteAllText('{escapedPidPath}',[string]$PID);" +
                    "[Console]::Error.WriteLine('bounded-child-secret');" +
                    "Start-Sleep -Seconds 60",
                "descendant_stress" =>
                    "$ids=[Collections.Generic.List[string]]::new();" +
                    "1..16|%{$p=Start-Process powershell.exe " +
                    "-WindowStyle Hidden " +
                    $"-ArgumentList '-NoProfile -EncodedCommand {nestedSleep}' " +
                    "-PassThru;$ids.Add([string]$p.Id)};" +
                    $"[IO.File]::WriteAllLines('{escapedPidPath}',$ids);" +
                    "Start-Sleep -Seconds 60",
                _ =>
                    $"[IO.File]::WriteAllText('{escapedPidPath}',[string]$PID);" +
                    "$chunk='bounded-child-secret'+('X'*2048);" +
                    "for($index=0;$index-lt512;$index++){" +
                    "[Console]::Out.Write($chunk)};" +
                    "Start-Sleep -Seconds 60"
            };
            var encodedChildCommand = Convert.ToBase64String(
                Encoding.Unicode.GetBytes(childCommand));
            File.WriteAllText(
                fakeDotnet,
                "@echo off" + Environment.NewLine +
                "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden " +
                "-ExecutionPolicy Bypass -EncodedCommand " +
                encodedChildCommand +
                Environment.NewLine,
                Encoding.ASCII);
            File.WriteAllText(
                harnessPath,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$FakeDotnet,
                    [Parameter(Mandatory = $true)][string]$TestRoot,
                    [Parameter(Mandatory = $true)][string]$FailureMode
                )

                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                foreach ($functionName in @(
                    'Initialize-StoredCredentialBoundedProcessCapture'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                $startedAt = [DateTime]::UtcNow
                Initialize-StoredCredentialBoundedProcessCapture
                $timeoutMilliseconds = if ($FailureMode -eq 'timeout') {
                    3000
                }
                elseif ($FailureMode -eq 'descendant_stress') {
                    10000
                }
                else {
                    10000
                }
                $maxElapsedSeconds = if ($FailureMode -eq 'descendant_stress') {
                    20
                }
                else {
                    15
                }
                $result =
                    [GeoraePlan.TestEnvironment.BoundedProcessCapture]::Run(
                        $FakeDotnet,
                        '',
                        $TestRoot,
                        $timeoutMilliseconds,
                        2048,
                        1024)
                $elapsed = [DateTime]::UtcNow - $startedAt
                $expectedReason = if ($FailureMode -eq 'descendant_stress') {
                    'timeout'
                }
                else {
                    $FailureMode
                }
                if ([string]$result.FailureReason -cne $expectedReason) {
                    throw (
                        'Unexpected bounded process failure reason: ' +
                        [string]$result.FailureReason +
                        '; exit_code=' + [string]$result.ExitCode +
                        '; child_started=' +
                        [string](Test-Path -LiteralPath (
                            Join-Path $TestRoot 'child.pid')))
                }
                if (
                    -not [string]::IsNullOrEmpty([string]$result.Stdout) -or
                    -not [string]::IsNullOrEmpty([string]$result.Stderr) -or
                    $elapsed.TotalSeconds -gt $maxElapsedSeconds
                ) {
                    throw 'Bounded process output or timeout was not contained.'
                }
                Write-Output "bounded_process_$FailureMode"
                """,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolvePreparationScript(),
                "-FakeDotnet",
                fakeDotnet,
                "-TestRoot",
                testRoot,
                "-FailureMode",
                failureMode);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                $"bounded_process_{failureMode}",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                secret,
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);

            await WaitForFileAsync(
                childPidPath,
                TimeSpan.FromSeconds(5));
            var childPids = File.ReadAllLines(childPidPath)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => int.Parse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Equal(
                failureMode == "descendant_stress" ? 16 : 1,
                childPids.Length);
            Assert.All(
                childPids,
                childPid => Assert.False(
                    IsProcessAlive(childPid),
                    "The bounded credential process left a child alive."));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task StoredCredentialEnvelopeProcess_SuccessClosesLingeringDescendantPipes()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-success-descendant-{Guid.NewGuid():N}");
        var fakeDotnet = Path.Combine(
            testRoot,
            "fake-dotnet.cmd");
        var childPidPath = Path.Combine(
            testRoot,
            "child.pid");
        var harnessPath = Path.Combine(
            testRoot,
            "harness.ps1");
        Directory.CreateDirectory(testRoot);

        try
        {
            var escapedPidPath =
                childPidPath.Replace("'", "''");
            var nestedSleep =
                Convert.ToBase64String(
                    Encoding.Unicode.GetBytes(
                        "Start-Sleep -Seconds 60"));
            var rootCommand =
                "[Console]::OutputEncoding=" +
                "[Text.UTF8Encoding]::new($false);" +
                "$child=Start-Process powershell.exe " +
                "-NoNewWindow " +
                $"-ArgumentList '-NoProfile -EncodedCommand {nestedSleep}' " +
                "-PassThru;" +
                $"[IO.File]::WriteAllText('{escapedPidPath}',[string]$child.Id);" +
                "[Console]::Out.WriteLine('root-success')";
            var encodedRootCommand =
                Convert.ToBase64String(
                    Encoding.Unicode.GetBytes(
                        rootCommand));
            File.WriteAllText(
                fakeDotnet,
                "@echo off" + Environment.NewLine +
                "powershell.exe -NoProfile -NonInteractive " +
                "-WindowStyle Hidden -ExecutionPolicy Bypass " +
                "-EncodedCommand " +
                encodedRootCommand +
                Environment.NewLine +
                "exit /b %ERRORLEVEL%" +
                Environment.NewLine,
                Encoding.ASCII);
            File.WriteAllText(
                harnessPath,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$FakeDotnet,
                    [Parameter(Mandatory = $true)][string]$TestRoot
                )

                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }
                $functionAst = $ast.Find({
                    param($node)
                    $node -is
                        [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq
                        'Initialize-StoredCredentialBoundedProcessCapture'
                }, $true)
                if ($null -eq $functionAst) {
                    throw 'Bounded process capture function was not found.'
                }
                . ([scriptblock]::Create($functionAst.Extent.Text))

                Initialize-StoredCredentialBoundedProcessCapture
                $startedAt = [DateTime]::UtcNow
                $result =
                    [GeoraePlan.TestEnvironment.BoundedProcessCapture]::Run(
                        $FakeDotnet,
                        '',
                        $TestRoot,
                        10000,
                        2048,
                        1024)
                $elapsed = [DateTime]::UtcNow - $startedAt
                if (
                    -not [string]::IsNullOrEmpty(
                        [string]$result.FailureReason) -or
                    [int]$result.ExitCode -ne 0 -or
                    [string]$result.Stdout -cnotmatch 'root-success' -or
                    $elapsed.TotalSeconds -gt 5
                ) {
                    throw (
                        'Successful bounded process did not close descendants. ' +
                        'exit_code=' + [string]$result.ExitCode +
                        ' failure_reason=' +
                        [string]$result.FailureReason +
                        ' elapsed_ms=' +
                        [string][int]$elapsed.TotalMilliseconds)
                }
                Write-Output 'success_descendant_cleanup_verified'
                """,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(20),
                "-SourceScript",
                ResolvePreparationScript(),
                "-FakeDotnet",
                fakeDotnet,
                "-TestRoot",
                testRoot);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout +
                Environment.NewLine +
                result.Stderr);
            Assert.Contains(
                "success_descendant_cleanup_verified",
                result.Stdout,
                StringComparison.Ordinal);
            await WaitForFileAsync(
                childPidPath,
                TimeSpan.FromSeconds(5));
            var childPid = int.Parse(
                File.ReadAllText(
                    childPidPath),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(
                IsProcessAlive(childPid),
                "The successful bounded process left a pipe-holding child alive.");
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(
                testRoot);
        }
    }

    [Fact]
    public void PreparationScript_SeedSyncRetriesAreBoundedAndFailClosed()
    {
        var sourceScript = ResolvePreparationScript();
        var source = File.ReadAllText(sourceScript);
        var syncDiagSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "Program.cs"));
        var seedFunctionIndex = source.IndexOf(
            "function Initialize-IsolatedServerData",
            StringComparison.Ordinal);
        var maxAttemptsIndex = source.IndexOf(
            "$maxSeedSyncAttempts = 3",
            seedFunctionIndex,
            StringComparison.Ordinal);
        var retryLoopIndex = source.IndexOf(
            "for ($seedSyncAttempt = 1; $seedSyncAttempt -le $maxSeedSyncAttempts; $seedSyncAttempt++)",
            maxAttemptsIndex,
            StringComparison.Ordinal);
        var perAttemptLogIndex = source.IndexOf(
            "\"seed-sync-attempt-{0}.log\"",
            retryLoopIndex,
            StringComparison.Ordinal);
        var cleanCompletionIndex = source.IndexOf(
            "[regex]::IsMatch($syncResult.Text, '(?m)^dirty_count=0\\s*$')",
            retryLoopIndex,
            StringComparison.Ordinal);
        var outboxCompletionIndex = source.IndexOf(
            "'(?m)^non_acknowledged_outbox_count=0\\s*$'",
            cleanCompletionIndex,
            StringComparison.Ordinal);
        var retryHealthCheckIndex = source.IndexOf(
            "시드 동기화 재시도 전에 종료되었습니다",
            outboxCompletionIndex,
            StringComparison.Ordinal);
        var retryBaseUrlIndex = source.IndexOf(
            "GEORAEPLAN_SYNC_BASEURL = ($serverState.ServerUrl + '/')",
            retryHealthCheckIndex,
            StringComparison.Ordinal);
        var retryPreparationIndex = source.IndexOf(
            "'prepare-test-seed-retry'",
            retryBaseUrlIndex,
            StringComparison.Ordinal);
        var failedSummaryIndex = source.IndexOf(
            "'seed_sync_succeeded=False'",
            retryPreparationIndex,
            StringComparison.Ordinal);
        var failClosedIndex = source.IndexOf(
            "throw $seedSyncWarning",
            failedSummaryIndex,
            StringComparison.Ordinal);
        var userBootstrapIndex = source.IndexOf(
            "$storedCredentials = if (",
            failClosedIndex,
            StringComparison.Ordinal);
        var attemptSummaryIndex = source.IndexOf(
            "\"seed_sync_attempts=$seedSyncAttemptCount\"",
            userBootstrapIndex,
            StringComparison.Ordinal);

        Assert.True(seedFunctionIndex >= 0, "The isolated seed function was not found.");
        Assert.True(
            maxAttemptsIndex > seedFunctionIndex,
            "The seed sync retry count is not explicitly bounded.");
        Assert.True(
            retryLoopIndex > maxAttemptsIndex,
            "The seed sync does not use its bounded retry count.");
        Assert.True(
            perAttemptLogIndex > retryLoopIndex,
            "The seed sync does not retain a log for every retry attempt.");
        Assert.True(
            cleanCompletionIndex > perAttemptLogIndex,
            "The seed sync can succeed without proving that no dirty rows remain.");
        Assert.True(
            outboxCompletionIndex > cleanCompletionIndex,
            "The seed sync can succeed with non-acknowledged outbox rows.");
        Assert.Contains(
            "non_acknowledged_outbox_count=",
            syncDiagSource,
            StringComparison.Ordinal);
        Assert.True(
            retryHealthCheckIndex > outboxCompletionIndex,
            "The isolated server is not checked before a seed retry.");
        Assert.True(
            retryBaseUrlIndex > retryHealthCheckIndex,
            "The seed retry tool is not bound to the attested loopback server URL.");
        Assert.True(
            retryPreparationIndex > retryBaseUrlIndex,
            "Dependent revisions are not prepared before a seed retry.");
        Assert.True(
            failedSummaryIndex > retryPreparationIndex,
            "An exhausted seed retry does not write a failed summary.");
        Assert.True(
            failClosedIndex > failedSummaryIndex,
            "The default seed path does not fail closed after bounded retries.");
        Assert.True(
            userBootstrapIndex > failClosedIndex,
            "User bootstrap can run before an incomplete seed is rejected.");
        Assert.True(
            attemptSummaryIndex > userBootstrapIndex,
            "The seed summary does not record the number of attempts.");
    }

    [Fact]
    public void PreparationScript_SeedStorageResetAndShutdownFailClosed()
    {
        var sourceScript = ResolvePreparationScript();
        var source = File.ReadAllText(sourceScript);
        var resetFunctionIndex = source.IndexOf(
            "function Reset-IsolatedServerStorage",
            StringComparison.Ordinal);
        var resetFunctionEnd = source.IndexOf(
            "function Repair-ProcessPathEnvironmentForChildProcess",
            resetFunctionIndex,
            StringComparison.Ordinal);
        var strictRemovalIndex = source.IndexOf(
            "Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop",
            resetFunctionIndex,
            StringComparison.Ordinal);
        var resetPostconditionIndex = source.IndexOf(
            "격리 테스트 서버의 이전 저장소를 제거하지 못했습니다",
            strictRemovalIndex,
            StringComparison.Ordinal);
        var stopFunctionIndex = source.IndexOf(
            "function Stop-IsolatedServerProcess",
            resetFunctionEnd,
            StringComparison.Ordinal);
        var taskkillIndex = source.IndexOf(
            "& taskkill /PID $State.Process.Id /T /F",
            stopFunctionIndex,
            StringComparison.Ordinal);
        var firstWaitIndex = source.IndexOf(
            "$State.Process.WaitForExit(5000)",
            taskkillIndex,
            StringComparison.Ordinal);
        var fallbackKillIndex = source.IndexOf(
            "$State.Process.Kill()",
            firstWaitIndex,
            StringComparison.Ordinal);
        var secondWaitIndex = source.IndexOf(
            "$State.Process.WaitForExit(5000)",
            firstWaitIndex + 1,
            StringComparison.Ordinal);
        var shutdownFailureIndex = source.IndexOf(
            "격리 테스트 서버 프로세스가 종료되지 않았습니다",
            secondWaitIndex,
            StringComparison.Ordinal);
        var disposeIndex = source.IndexOf(
            "$State.Process.Dispose()",
            shutdownFailureIndex,
            StringComparison.Ordinal);

        Assert.True(
            resetFunctionIndex >= 0 && resetFunctionEnd > resetFunctionIndex,
            "The isolated server storage reset function was not found.");
        Assert.True(
            strictRemovalIndex > resetFunctionIndex &&
            strictRemovalIndex < resetFunctionEnd,
            "Stale isolated server storage removal can silently fail.");
        Assert.True(
            resetPostconditionIndex > strictRemovalIndex &&
            resetPostconditionIndex < resetFunctionEnd,
            "Stale isolated server storage removal has no postcondition.");
        Assert.True(
            stopFunctionIndex > resetFunctionEnd,
            "The isolated seed server stop function was not found.");
        Assert.True(
            taskkillIndex > stopFunctionIndex,
            "The isolated seed server process tree is not terminated.");
        Assert.True(
            firstWaitIndex > taskkillIndex,
            "The isolated seed server shutdown is not awaited.");
        Assert.True(
            fallbackKillIndex > firstWaitIndex,
            "The isolated seed server shutdown has no process-handle fallback.");
        Assert.True(
            secondWaitIndex > fallbackKillIndex,
            "The fallback seed server shutdown is not awaited.");
        Assert.True(
            shutdownFailureIndex > secondWaitIndex,
            "Seed server shutdown failure is not propagated.");
        Assert.True(
            disposeIndex > shutdownFailureIndex,
            "The isolated seed server process handle is not disposed.");
    }

    [Fact]
    public void SeedTools_RequireIsolatedMarkerUseTransactionsAndLeaseEveryWritableRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var preparationSource = File.ReadAllText(ResolvePreparationScript());
        var syncDiagPath = Path.Combine(
            repositoryRoot,
            "tools",
            "SyncDiag",
            "Program.cs");
        var syncDiagSource = File.ReadAllText(syncDiagPath);

        var seedGuardIndex = syncDiagSource.IndexOf(
            "AssertIsolatedTestSeedCommandEnvironment();",
            StringComparison.Ordinal);
        var databaseLeaseIndex = syncDiagSource.IndexOf(
            "IsolatedPreparationDatabaseLease.AcquireForAppData(",
            seedGuardIndex >= 0 ? seedGuardIndex : 0,
            StringComparison.Ordinal);
        var leaseSelectionStart = syncDiagSource.IndexOf(
            "static bool RequiresIsolatedTestDatabaseLease",
            StringComparison.Ordinal);
        var leaseSelectionEnd = leaseSelectionStart >= 0
            ? syncDiagSource.IndexOf(
                "static bool IsAlwaysIsolatedTestSeedCommand",
                leaseSelectionStart,
                StringComparison.Ordinal)
            : -1;
        var serverFinalizerIndex = syncDiagSource.IndexOf(
            "\"finalize-test-server-sqlite\"",
            StringComparison.Ordinal);
        var dbInitializationIndex = syncDiagSource.IndexOf(
            "await using var db = new LocalDbContext();",
            StringComparison.Ordinal);
        var markStart = syncDiagSource.IndexOf(
            "static async Task<int> MarkAllDirtyAsync",
            StringComparison.Ordinal);
        var markEnd = syncDiagSource.IndexOf(
            "static async Task<int> MarkDirtyAsync",
            markStart,
            StringComparison.Ordinal);
        var retryStart = syncDiagSource.IndexOf(
            "PrepareTestSeedRetryAsync(LocalDbContext db)",
            markEnd,
            StringComparison.Ordinal);
        var retryEnd = syncDiagSource.IndexOf(
            "static bool IsAlwaysIsolatedTestSeedCommand",
            retryStart,
            StringComparison.Ordinal);
        var guardStart = retryEnd >= 0
            ? syncDiagSource.IndexOf(
                "static void AssertIsolatedTestSeedCommandEnvironment",
                retryEnd,
                StringComparison.Ordinal)
            : -1;
        var guardEnd = guardStart >= 0
            ? syncDiagSource.IndexOf(
                "static bool IsTruthy",
                guardStart,
                StringComparison.Ordinal)
            : -1;

        Assert.True(
            seedGuardIndex >= 0 &&
            databaseLeaseIndex > seedGuardIndex &&
            databaseLeaseIndex < dbInitializationIndex,
            "Seed command isolation is not checked before opening the mutable local database.");
        Assert.True(
            serverFinalizerIndex >= 0 &&
            serverFinalizerIndex < dbInitializationIndex,
            "Server SQLite finalization is not dispatched before opening the desktop local database.");
        Assert.True(
            leaseSelectionStart >= 0 && leaseSelectionEnd > leaseSelectionStart,
            "The isolated database lease command selector was not found.");
        var leaseSelectionSource =
            syncDiagSource[leaseSelectionStart..leaseSelectionEnd];
        Assert.Contains(
            "GEORAEPLAN_TEST_SEED_MODE",
            leaseSelectionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(value, \"inspect\", StringComparison.Ordinal)",
            leaseSelectionSource,
            StringComparison.Ordinal);
        Assert.True(
            markStart >= 0 && markEnd > markStart,
            "The mark-all-dirty implementation was not found.");
        Assert.Contains(
            "BeginTransactionAsync",
            syncDiagSource[markStart..markEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "CommitAsync",
            syncDiagSource[markStart..markEnd],
            StringComparison.Ordinal);
        Assert.True(
            retryStart >= 0 && retryEnd > retryStart,
            "The seed retry preparation implementation was not found.");
        Assert.Contains(
            "BeginTransactionAsync",
            syncDiagSource[retryStart..retryEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "CommitAsync",
            syncDiagSource[retryStart..retryEnd],
            StringComparison.Ordinal);
        Assert.True(
            guardStart >= 0 && guardEnd > guardStart,
            "The isolated seed command environment guard was not found.");
        var guardSource = syncDiagSource[guardStart..guardEnd];
        Assert.Contains("GEORAEPLAN_TEST_MODE", guardSource, StringComparison.Ordinal);
        Assert.Contains("GEORAEPLAN_TEST_SEED_MODE", guardSource, StringComparison.Ordinal);
        Assert.Contains("GEORAEPLAN_TEST_SEED_ROOT", guardSource, StringComparison.Ordinal);
        Assert.Contains(".georaeplan-isolated-seed-root", guardSource, StringComparison.Ordinal);
        Assert.Contains(
            "normal V1 application data root",
            guardSource,
            StringComparison.Ordinal);
        Assert.Contains("AssertNoReparsePointAncestors", guardSource, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", syncDiagSource, StringComparison.Ordinal);
        var readOnlySummaryStart = guardEnd >= 0
            ? syncDiagSource.IndexOf(
                "static int PrintReadOnlyDatabaseSummary",
                guardEnd,
                StringComparison.Ordinal)
            : -1;
        var readOnlySummaryEnd = readOnlySummaryStart >= 0
            ? syncDiagSource.IndexOf(
                "static IReadOnlyList<SchemaObject> ReadSchemaObjects",
                readOnlySummaryStart,
                StringComparison.Ordinal)
            : -1;
        Assert.True(
            readOnlySummaryStart >= 0 && readOnlySummaryEnd > readOnlySummaryStart,
            "The read-only database summary implementation was not found.");
        var readOnlySummarySource = syncDiagSource[readOnlySummaryStart..readOnlySummaryEnd];
        Assert.Contains("FileShare.Read", readOnlySummarySource, StringComparison.Ordinal);
        Assert.Contains("snapshotLease.Length", readOnlySummarySource, StringComparison.Ordinal);
        Assert.Contains("snapshotLastWriteUtc", readOnlySummarySource, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(readOnlySummarySource, "sidecarPaths.Any(File.Exists)") >= 3,
            "The read-only summary does not recheck sidecars after acquiring and using its snapshot lease.");

        Assert.Contains(
            "foreach ($childName in @(",
            preparationSource,
            StringComparison.Ordinal);
        foreach (var writableChild in new[]
                 {
                     "App",
                     "Server",
                     "AppData",
                     "ServerData",
                     "RuntimeLogs"
                 })
        {
            Assert.Contains(
                $"'{writableChild}'",
                preparationSource,
                StringComparison.Ordinal);
        }
        Assert.Contains(
            "top-level reparse point found",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "nested file has multiple hard links",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ".georaeplan-isolated-seed-root",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_TEST_SEED_ROOT = $TestAppRoot",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileShare]::None",
            preparationSource,
            StringComparison.Ordinal);
        var preparationLifetimeLeaseStart = preparationSource.LastIndexOf(
            "$preparationLease = [IO.File]::Open(",
            StringComparison.Ordinal);
        var preparationLifetimeLeaseEnd = preparationLifetimeLeaseStart >= 0
            ? preparationSource.IndexOf(
                "Assert-PreparationExclusionLease `",
                preparationLifetimeLeaseStart,
                StringComparison.Ordinal)
            : -1;
        Assert.True(
            preparationLifetimeLeaseStart >= 0 &&
            preparationLifetimeLeaseEnd > preparationLifetimeLeaseStart,
            "The parent preparation lifetime lease block was not found.");
        var preparationLifetimeLeaseSource = preparationSource[
            preparationLifetimeLeaseStart..preparationLifetimeLeaseEnd];
        Assert.Contains(
            "[IO.FileShare]::Read",
            preparationLifetimeLeaseSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[IO.FileShare]::None",
            preparationLifetimeLeaseSource,
            StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(
                preparationSource,
                "Assert-SafeTestEnvironmentOutputRoot `") >= 4,
            "Writable output roots are not revalidated throughout preparation.");
        Assert.Contains(
            "-AllowDirtySeedFailure is no longer supported",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Copy-StableStandaloneSqliteSnapshot",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-CopiedSnapshotTargetSafeForRemoval",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileMode]::CreateNew",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "snapshot_child_output_redacted=True",
            preparationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "+ $snapshotResult.Text",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileShare]::Read",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-FileHash -LiteralPath $SourceDatabase -Algorithm SHA256",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "data\\거래플랜.db-journal",
            preparationSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyInvoiceCanonicalization_IsExplicitFreshCopyOnlyAndPrecedesDirtyMarking()
    {
        var repositoryRoot = FindRepositoryRoot();
        var preparationSource =
            File.ReadAllText(ResolvePreparationScript());
        var syncDiagSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "SyncDiag",
            "Program.cs"));
        var canonicalizerSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "SyncDiag",
            "IsolatedLegacyInvoiceSeedCanonicalizer.cs"));
        var testProjectSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Tests",
            "GeoraePlan.Desktop.App.Tests",
            "GeoraePlan.Desktop.App.Tests.csproj"));

        Assert.Contains(
            "[switch]$CanonicalizeLegacyInvoiceSeed",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "-SkipDataCopy is not allowed",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ".georaeplan-isolated-seed-source-attestation.json",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "legacy-invoice-seed-canonicalization.success",
            preparationSource,
            StringComparison.Ordinal);

        var initializeStart = preparationSource.IndexOf(
            "function Initialize-IsolatedServerData",
            StringComparison.Ordinal);
        var initializeEnd = preparationSource.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            initializeStart,
            StringComparison.Ordinal);
        Assert.True(
            initializeStart >= 0 && initializeEnd > initializeStart);
        var initializeSource =
            preparationSource[initializeStart..initializeEnd];
        var prepareIndex = initializeSource.IndexOf(
            "'prepare-test-seed'",
            StringComparison.Ordinal);
        var canonicalizeIndex = initializeSource.IndexOf(
            "'canonicalize-legacy-invoice-test-seed'",
            StringComparison.Ordinal);
        var dirtyIndex = initializeSource.IndexOf(
            "'mark-all-dirty'",
            StringComparison.Ordinal);
        Assert.True(
            prepareIndex >= 0 &&
            canonicalizeIndex > prepareIndex &&
            dirtyIndex > canonicalizeIndex);
        Assert.Equal(
            1,
            CountOccurrences(
                initializeSource,
                "'canonicalize-legacy-invoice-test-seed'"));

        Assert.Contains(
            "canonicalize-legacy-invoice-test-seed",
            syncDiagSource,
            StringComparison.Ordinal);
        var canonicalizeCommandStart = syncDiagSource.IndexOf(
            "case \"canonicalize-legacy-invoice-test-seed\":",
            StringComparison.Ordinal);
        var canonicalizeCommandEnd = syncDiagSource.IndexOf(
            "case \"prepare-test-seed-retry\":",
            canonicalizeCommandStart,
            StringComparison.Ordinal);
        Assert.True(
            canonicalizeCommandStart >= 0 &&
            canonicalizeCommandEnd > canonicalizeCommandStart);
        var canonicalizeCommandSource =
            syncDiagSource[canonicalizeCommandStart..canonicalizeCommandEnd];
        Assert.Contains(
            "catch (Exception ex)",
            canonicalizeCommandSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "BuildSanitizedCanonicalizationError(ex)",
            canonicalizeCommandSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Console.Error.WriteLine(ex)",
            canonicalizeCommandSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "reason_code=",
            syncDiagSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "group_fingerprint_sha256=",
            syncDiagSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception ex) when (",
            syncDiagSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"canonicalize-legacy-invoice-test-seed\",",
            syncDiagSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"preview-legacy-invoice-test-seed\",",
            syncDiagSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "baseUri.IsLoopback",
            syncDiagSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_TEST_SERVER_ROOT",
            syncDiagSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_TEST_SERVER_BASEURL",
            syncDiagSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_TEST_SEED_CANONICALIZE_LEGACY_INVOICES",
            canonicalizerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "active_operational_seed_only_not_deleted_history_migration",
            canonicalizerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$approvedLegacyInvoiceSeedSourceDatabaseSha256Values = @(",
            preparationSource,
            StringComparison.Ordinal);
        foreach (var approvedSourceHash in new[]
                 {
                      "795B5A6CA153B788C6272222D778D714DB10873541775493AB7B36EA091E2FBE",
                      "E98DF3E657205319F595AE61089F50E1B87F0BD272C650827AA123B4A8616916",
                      "719380E811BB04DC364FB6D2E0BD4C4E04B3D3C12F4D56207233D600F80B9A5C",
                      "F422BC337476CE0A6A47638A1CF6D1F1CE1103ED81EF02688C8382197BBD8BA1",
                      "937B93127A721A16857403DE5B3B7DDD7669C1787AC0EAD9C32C83A413B37FE2"
                  })
        {
            Assert.Contains(
                approvedSourceHash,
                preparationSource,
                StringComparison.Ordinal);
        }
        Assert.Contains(
            "Assert-LegacyInvoiceCanonicalizationReportProfile",
            preparationSource,
            StringComparison.Ordinal);
        foreach (var expectedProfileValue in new[]
                 {
                     "8A324FC2831CF3C8F996D8D6EA6B7AD01EDBFB7E793C5CB0548ED534F960904D",
                     "3EE8A9B5E52A2AD014AB9FFD65574D70A562E867B0C12256CA7BB7168AE1230B",
                     "0D2CCBFEDEDA9540F4C5898187BAA7BFC3418D6272112C01772C7CE834AB076E",
                     "C80296708B5E84B5401D1D393CFA5FD2D117708C4B3F611BD3156330469D01EA",
                     "6F7DA4EFEE728601EF5AADBC60F0AB08C59DA70A3A7D49D7B74BBA652DD1ECB9",
                     "EE5B6FC6E2C9D58B3FBC066E00C95693F8EBC63DFE1BC1FCE784EB80EDF85CE8",
                     "D5528F8C6750119E3D642C0953C8C2519CB88C1E6E37457C81868839649641F7",
                     "deleted_predecessor_active_chain_reroot",
                     "duplicate_sibling_linearize",
                     "historical_responsible_office_align"
                 })
        {
            Assert.Contains(
                expectedProfileValue,
                preparationSource,
                StringComparison.Ordinal);
        }
        var canonicalizationBlockStart = preparationSource.IndexOf(
            "$canonicalizationLogPath =",
            initializeStart,
            StringComparison.Ordinal);
        var canonicalizationBlockEnd = preparationSource.IndexOf(
            "$seedPort = Get-FreeTcpPort",
            canonicalizationBlockStart,
            StringComparison.Ordinal);
        Assert.True(
            canonicalizationBlockStart >= 0 &&
            canonicalizationBlockEnd > canonicalizationBlockStart);
        var canonicalizationBoundarySource =
            preparationSource[
                canonicalizationBlockStart..canonicalizationBlockEnd];
        Assert.DoesNotContain(
            "-Content $canonicalizationResult.Text",
            canonicalizationBoundarySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "+ $canonicalizationResult.Text",
            canonicalizationBoundarySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "child_output_redacted=True",
            canonicalizationBoundarySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "'groupOrdinal'",
            preparationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'groupId'",
            preparationSource[
                preparationSource.IndexOf(
                    "function Assert-LegacyInvoiceCanonicalizationReportProfile",
                    StringComparison.Ordinal)..
                preparationSource.IndexOf(
                    "function Initialize-IsolatedServerData",
                    StringComparison.Ordinal)],
            StringComparison.Ordinal);
        var canonicalizationGateStart = preparationSource.IndexOf(
            "if ($CanonicalizeLegacyInvoiceSeed) {",
            initializeEnd,
            StringComparison.Ordinal);
        var buildEnvironmentIndex = preparationSource.IndexOf(
            "Initialize-IsolatedBuildEnvironmentOnD",
            canonicalizationGateStart,
            StringComparison.Ordinal);
        var outputMutationIndex = preparationSource.IndexOf(
            "New-Item -ItemType Directory -Force -Path $OutputRoot",
            canonicalizationGateStart,
            StringComparison.Ordinal);
        var approvedHashGateIndex = preparationSource.IndexOf(
            "$approvedLegacyInvoiceSeedSourceDatabaseSha256Values",
            canonicalizationGateStart,
            StringComparison.Ordinal);
        Assert.True(
            canonicalizationGateStart >= 0 &&
            approvedHashGateIndex > canonicalizationGateStart &&
            approvedHashGateIndex < buildEnvironmentIndex &&
            approvedHashGateIndex < outputMutationIndex);
        Assert.Contains(
            "partial_push_outbox_present",
            canonicalizerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsolatedLegacyInvoiceSeedCanonicalizer.cs",
            testProjectSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "remark-legacy-invoice-test-seed-tombstones",
            preparationSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RunAll_FinalizesServerSqliteThroughCertifiedServerAfterShutdown()
    {
        var repositoryRoot = FindRepositoryRoot();
        var preparationSource = File.ReadAllText(ResolvePreparationScript());
        var generatedRunAllSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "테스트 시행",
            "실행환경",
            "Run-All.ps1"));
        var serverProgramSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Server",
            "거래플랜.Server.Api",
            "Program.cs"));
        var serverProjectSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Server",
            "거래플랜.Server.Api",
            "거래플랜.Server.Api.csproj"));
        var finalizerSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "SyncDiag",
            "IsolatedTestServerSqliteFinalizer.cs"));

        foreach (var runAllSource in new[]
                 {
                     preparationSource,
                     generatedRunAllSource
                 })
        {
            var functionStart = runAllSource.IndexOf(
                "function Invoke-IsolatedServerSqliteFinalizer",
                StringComparison.Ordinal);
            var invocationIndex = runAllSource.LastIndexOf(
                "Invoke-IsolatedServerSqliteFinalizer `",
                StringComparison.Ordinal);
            var failureIndex = runAllSource.IndexOf(
                "Server SQLite finalization failed after runtime shutdown.",
                invocationIndex,
                StringComparison.Ordinal);

            Assert.True(
                functionStart >= 0,
                "The Run-All server SQLite finalizer function was not found.");
            Assert.Contains(
                "--finalize-isolated-test-sqlite",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "$startInfo.EnvironmentVariables['GEORAEPLAN_TEST_MODE'] = '1'",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "$startInfo.EnvironmentVariables['GEORAEPLAN_TEST_SEED_MODE'] = '1'",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "$startInfo.EnvironmentVariables['GEORAEPLAN_TEST_SERVER_ROOT']",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "$ProcessJob.AssignProcess($process)",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "The uncontained server SQLite finalizer did not",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "assignment failed and process cleanup failed.",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "assigned to the launcher job.",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "server-sqlite-finalize.log",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "$values['server_sqlite_finalized'] -cne 'True'",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "$sidecarCount -ne 0",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "$values['database_sha256'] -notmatch '^[0-9A-F]{64}$'",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "'^(?<key>[a-z][a-z0-9_]*)=(?<value>.*)$'",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "'^(?<key>[a-z_]+)=(?<value>.*)$'",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "$finalizationMessage =",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            Assert.Contains(
                "Write-Log $finalizationMessage",
                runAllSource[functionStart..],
                StringComparison.Ordinal);
            var timeoutIndex = runAllSource.IndexOf(
                "$process.WaitForExit(30000)",
                functionStart,
                StringComparison.Ordinal);
            var boundedTerminationIndex = runAllSource.IndexOf(
                "$process.WaitForExit(5000)",
                timeoutIndex,
                StringComparison.Ordinal);
            var timeoutFailureIndex = runAllSource.IndexOf(
                "did not exit within five seconds after termination.",
                boundedTerminationIndex,
                StringComparison.Ordinal);
            Assert.True(
                timeoutIndex >= functionStart &&
                boundedTerminationIndex > timeoutIndex &&
                timeoutFailureIndex > boundedTerminationIndex,
                "The finalizer timeout path can wait indefinitely after termination.");
            Assert.True(
                invocationIndex >= 0 &&
                failureIndex > invocationIndex,
                "Run-All does not finalize SQLite after server shutdown and propagate failure.");
            var cleanupStart = runAllSource.LastIndexOf(
                "finally {",
                invocationIndex,
                StringComparison.Ordinal);
            Assert.True(
                cleanupStart >= 0,
                "The Run-All cleanup block was not found.");
            var cleanupSource = runAllSource[cleanupStart..]
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            var exitStateIndex = cleanupSource.IndexOf(
                "$serverExitConfirmed = ($null -eq $serverProcess)",
                StringComparison.Ordinal);
            var normalKillIndex = cleanupSource.IndexOf(
                "$serverProcess.Kill()",
                exitStateIndex,
                StringComparison.Ordinal);
            var failedJobCaptureIndex = cleanupSource.IndexOf(
                "$failedChildProcessJob = $childProcessJob",
                normalKillIndex,
                StringComparison.Ordinal);
            var failedJobDisposeIndex = cleanupSource.IndexOf(
                "$failedChildProcessJob.Dispose()",
                failedJobCaptureIndex,
                StringComparison.Ordinal);
            var fallbackWaitIndex = cleanupSource.IndexOf(
                "$serverProcess.WaitForExit(5000)",
                failedJobDisposeIndex,
                StringComparison.Ordinal);
            var unconfirmedFailureIndex = cleanupSource.IndexOf(
                "Server exit could not be confirmed; SQLite finalization was skipped.",
                fallbackWaitIndex,
                StringComparison.Ordinal);
            var finalizerExitGateIndex = cleanupSource.IndexOf(
                "if (\n" +
                "        $serverWasStarted -and\n" +
                "        $serverExitConfirmed -and\n" +
                "        $null -ne $childProcessJob\n" +
                "    ) {",
                unconfirmedFailureIndex,
                StringComparison.Ordinal);
            var gatedInvocationIndex = cleanupSource.IndexOf(
                "Invoke-IsolatedServerSqliteFinalizer `",
                finalizerExitGateIndex,
                StringComparison.Ordinal);
            Assert.True(
                exitStateIndex >= 0 &&
                normalKillIndex > exitStateIndex &&
                failedJobCaptureIndex > normalKillIndex &&
                failedJobDisposeIndex > failedJobCaptureIndex &&
                fallbackWaitIndex > failedJobDisposeIndex &&
                unconfirmedFailureIndex > fallbackWaitIndex &&
                finalizerExitGateIndex > unconfirmedFailureIndex &&
                gatedInvocationIndex > finalizerExitGateIndex,
                "Run-All can finalize SQLite without positively confirming server exit.");
            Assert.Contains(
                "$childProcessJob = $null",
                cleanupSource[failedJobCaptureIndex..failedJobDisposeIndex],
                StringComparison.Ordinal);
            Assert.Contains(
                "[GeoraePlan.Runtime.ChildProcessJob]::new()",
                cleanupSource[fallbackWaitIndex..finalizerExitGateIndex],
                StringComparison.Ordinal);
            var finalizerJobArgumentIndex = runAllSource.IndexOf(
                "-ProcessJob $childProcessJob",
                invocationIndex,
                StringComparison.Ordinal);
            var jobDisposeIndex = runAllSource.IndexOf(
                "$childProcessJob.Dispose()",
                invocationIndex,
                StringComparison.Ordinal);
            Assert.True(
                finalizerJobArgumentIndex > invocationIndex &&
                jobDisposeIndex > finalizerJobArgumentIndex,
                "The finalizer process is not protected by the launcher kill-on-close job.");
            Assert.DoesNotContain(
                "Remove-Item -LiteralPath $databasePath",
                runAllSource[functionStart..invocationIndex],
                StringComparison.Ordinal);
        }

        var commandIndex = serverProgramSource.IndexOf(
            "--finalize-isolated-test-sqlite",
            StringComparison.Ordinal);
        var builderIndex = serverProgramSource.IndexOf(
            "WebApplication.CreateBuilder(args)",
            StringComparison.Ordinal);
        Assert.True(
            commandIndex >= 0 && commandIndex < builderIndex,
            "The finalizer CLI is not dispatched before normal server startup.");
        foreach (var outputKey in new[]
                 {
                     "server_sqlite_finalized=True",
                     "checkpoint_busy=",
                     "checkpoint_log_frames=",
                     "checkpointed_frames=",
                     "journal_mode=",
                     "quick_check=",
                     "sidecar_count=",
                     "database_length=",
                     "database_sha256="
                 })
        {
            Assert.Contains(outputKey, serverProgramSource, StringComparison.Ordinal);
        }
        Assert.Contains(
            "server_sqlite_finalize_error=",
            serverProgramSource,
            StringComparison.Ordinal);

        Assert.Contains(
            "GEORAEPLAN_SERVER",
            serverProjectSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsolatedTestServerSqliteFinalizer.cs",
            serverProjectSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "#if !GEORAEPLAN_SERVER",
            finalizerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ServerSeed_FinalizesServerAndAppWalBeforeSuccessSummary()
    {
        var repositoryRoot = FindRepositoryRoot();
        var preparationSource = File.ReadAllText(ResolvePreparationScript());
        var finalizerSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "SyncDiag",
            "IsolatedTestServerSqliteFinalizer.cs"));

        var initializeStart = preparationSource.IndexOf(
            "function Initialize-IsolatedServerData",
            StringComparison.Ordinal);
        var initializeEnd = preparationSource.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            initializeStart,
            StringComparison.Ordinal);
        Assert.True(
            initializeStart >= 0 && initializeEnd > initializeStart,
            "The isolated server seed function was not found.");
        var initializeSource =
            preparationSource[initializeStart..initializeEnd];

        var markerIndex = initializeSource.IndexOf(
            ".georaeplan-isolated-server-root",
            StringComparison.Ordinal);
        var firstServerStartIndex = initializeSource.IndexOf(
            "$serverState = Start-IsolatedServerProcess",
            StringComparison.Ordinal);
        var firstServerStopIndex = initializeSource.IndexOf(
            "Stop-IsolatedServerProcess -State $serverState",
            firstServerStartIndex,
            StringComparison.Ordinal);
        var firstFinalizationIndex = initializeSource.IndexOf(
            "$initialFinalization = Complete-IsolatedServerSqliteSnapshot",
            firstServerStopIndex,
            StringComparison.Ordinal);
        var restartIndex = initializeSource.IndexOf(
            "$restartSmokeState = Start-IsolatedServerProcess",
            firstFinalizationIndex,
            StringComparison.Ordinal);
        var restartHealthIndex = initializeSource.IndexOf(
            "Wait-HttpReady `",
            restartIndex,
            StringComparison.Ordinal);
        var restartStopIndex = initializeSource.IndexOf(
            "Stop-IsolatedServerProcess -State $restartSmokeState",
            restartHealthIndex,
            StringComparison.Ordinal);
        var finalFinalizationIndex = initializeSource.IndexOf(
            "$finalFinalization = Complete-IsolatedServerSqliteSnapshot",
            restartStopIndex,
            StringComparison.Ordinal);
        var appFinalizationIndex = initializeSource.IndexOf(
            "$appFinalization = Complete-IsolatedAppSqliteSnapshot",
            finalFinalizationIndex,
            StringComparison.Ordinal);
        var successSummaryIndex = initializeSource.LastIndexOf(
            "Write-Utf8File -Path (Join-Path $SeedLogRoot 'seed-summary.txt')",
            StringComparison.Ordinal);

        Assert.True(
            markerIndex >= 0 && markerIndex < firstServerStartIndex,
            "The isolated server marker is not written before server startup.");
        Assert.True(
            firstServerStopIndex > firstServerStartIndex,
            "The seed server is not stopped before SQLite finalization.");
        Assert.True(
            firstFinalizationIndex > firstServerStopIndex,
            "The initial SQLite finalization does not follow seed server shutdown.");
        Assert.True(
            restartIndex > firstFinalizationIndex &&
            restartHealthIndex > restartIndex &&
            restartStopIndex > restartHealthIndex,
            "The finalized SQLite database is not restart/health checked and stopped.");
        Assert.True(
            finalFinalizationIndex > restartStopIndex,
            "SQLite is not finalized again after the restart smoke.");
        Assert.True(
            appFinalizationIndex > finalFinalizationIndex,
            "The isolated app SQLite database is not finalized after seed operations.");
        Assert.True(
            successSummaryIndex > appFinalizationIndex,
            "Seed success is written before the final server/app WAL checkpoints complete.");

        Assert.Contains(
            "PRAGMA wal_checkpoint(TRUNCATE);",
            finalizerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PRAGMA journal_mode=DELETE;",
            finalizerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_TEST_SERVER_ROOT",
            finalizerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ".georaeplan-isolated-server-root",
            finalizerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "FileShare.None",
            finalizerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "immutable=1",
            finalizerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparationScript_RejectsCDriveOutputBeforeCreatingIt()
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-c-drive-rejection-{Guid.NewGuid():N}");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);

        var fixture = CreatePreparationScriptFixture(sourceScript, testRoot);
        var outputRoot = Path.Combine(
            "C:\\",
            $"georaeplan-c-output-must-not-exist-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(outputRoot));

        try
        {
            var result = await RunPreparationScriptAsync(
                fixture,
                outputRoot,
                TimeSpan.FromSeconds(30));
            var diagnostic =
                result.Stdout + Environment.NewLine + result.Stderr;
            var dotnetInvocations = File.Exists(fixture.DotnetInvocationLog)
                ? File.ReadAllText(fixture.DotnetInvocationLog)
                : string.Empty;

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "writable roots must stay on D:",
                diagnostic,
                StringComparison.Ordinal);
            Assert.False(
                Directory.Exists(outputRoot),
                "Preparation wrote to C: before rejecting the output root.");
            Assert.DoesNotContain(
                "publish",
                dotnetInvocations,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Theory]
    [InlineData("ProjectRoot")]
    [InlineData("SourceAppRoot")]
    [InlineData("ScriptRoot")]
    [InlineData("DesktopSourceRoot")]
    [InlineData("ServerSourceRoot")]
    public async Task PreparationScript_RejectsProtectedOutputRootsBeforePublishingOrDeleting(
        string protectedRootName)
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-output-root-guard-{Guid.NewGuid():N}");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);

        var fixture = CreatePreparationScriptFixture(sourceScript, testRoot);
        var outputRoot = protectedRootName switch
        {
            "ProjectRoot" => fixture.ProjectRoot,
            "SourceAppRoot" => fixture.SourceAppRoot,
            "ScriptRoot" => fixture.ScriptRoot,
            "DesktopSourceRoot" => Path.Combine(
                fixture.ProjectRoot,
                "Desktop",
                "Fixture.Desktop.App"),
            "ServerSourceRoot" => Path.Combine(
                fixture.ProjectRoot,
                "Server",
                "Fixture.Server.Api"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(protectedRootName),
                protectedRootName,
                "Unknown protected root.")
        };
        var sourceMarker = Path.Combine(outputRoot, "App", "source-marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceMarker)!);
        File.WriteAllText(sourceMarker, "must survive");

        try
        {
            var result = await RunPreparationScriptAsync(
                fixture,
                outputRoot,
                TimeSpan.FromSeconds(15));

            AssertPreparationFailedClosed(
                result,
                fixture.DotnetInvocationLog,
                sourceMarker,
                protectedRootName);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_RejectsJunctionAliasToProjectRootBeforePublishingOrDeleting()
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-output-junction-guard-{Guid.NewGuid():N}");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);

        var fixture = CreatePreparationScriptFixture(sourceScript, testRoot);
        var outputRoot = Path.Combine(testRoot, "project-root-alias");
        var sourceMarker = Path.Combine(
            fixture.ProjectRoot,
            "App",
            "source-marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceMarker)!);
        File.WriteAllText(sourceMarker, "must survive");
        CreateDirectoryJunction(outputRoot, fixture.ProjectRoot);

        try
        {
            var result = await RunPreparationScriptAsync(
                fixture,
                outputRoot,
                TimeSpan.FromSeconds(15));

            AssertPreparationFailedClosed(
                result,
                fixture.DotnetInvocationLog,
                sourceMarker,
                "a junction alias to ProjectRoot");
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot);

            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Theory]
    [InlineData("SourceAppRoot", "Ancestor")]
    [InlineData("SourceAppRoot", "Descendant")]
    [InlineData("DesktopSourceRoot", "Ancestor")]
    [InlineData("DesktopSourceRoot", "Descendant")]
    [InlineData("ServerSourceRoot", "Ancestor")]
    [InlineData("ServerSourceRoot", "Descendant")]
    public async Task PreparationScript_RejectsProtectedRootOverlapBeforePublishingOrDeleting(
        string protectedRootName,
        string relationship)
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-output-overlap-guard-{Guid.NewGuid():N}");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);

        var fixture = CreatePreparationScriptFixture(sourceScript, testRoot);
        var protectedRoot = protectedRootName switch
        {
            "SourceAppRoot" => fixture.SourceAppRoot,
            "DesktopSourceRoot" => Path.Combine(
                fixture.ProjectRoot,
                "Desktop",
                "Fixture.Desktop.App"),
            "ServerSourceRoot" => Path.Combine(
                fixture.ProjectRoot,
                "Server",
                "Fixture.Server.Api"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(protectedRootName),
                protectedRootName,
                "Unknown protected root.")
        };
        var outputRoot = relationship switch
        {
            "Ancestor" => Path.GetDirectoryName(protectedRoot)
                          ?? throw new InvalidOperationException(
                              $"Protected root has no parent: {protectedRoot}"),
            "Descendant" => Path.Combine(
                protectedRoot,
                "nested-isolated-output"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(relationship),
                relationship,
                "Unknown path relationship.")
        };
        var deletionMarker = Path.Combine(
            outputRoot,
            "App",
            "must-survive.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(deletionMarker)!);
        File.WriteAllText(deletionMarker, "must survive");

        try
        {
            var result = await RunPreparationScriptAsync(
                fixture,
                outputRoot,
                TimeSpan.FromSeconds(15));

            AssertPreparationFailedClosed(
                result,
                fixture.DotnetInvocationLog,
                deletionMarker,
                $"{relationship} overlap with {protectedRootName}");
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Theory]
    [InlineData("App")]
    [InlineData("Server")]
    [InlineData("AppData")]
    [InlineData("ServerData")]
    [InlineData("RuntimeLogs")]
    [InlineData("Mobile")]
    public async Task PreparationScript_RejectsChildJunctionEscapingOutputRootBeforePublishingOrDeleting(
        string childName)
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-output-child-junction-guard-{Guid.NewGuid():N}");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);

        var fixture = CreatePreparationScriptFixture(sourceScript, testRoot);
        var outputRoot = Path.Combine(testRoot, "isolated-output");
        var escapedChildRoot = Path.Combine(outputRoot, childName);
        var sourceMarker = Path.Combine(
            fixture.ProjectRoot,
            "App",
            "source-marker.txt");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceMarker)!);
        File.WriteAllText(sourceMarker, "must survive");
        CreateDirectoryJunction(escapedChildRoot, fixture.ProjectRoot);

        try
        {
            var result = await RunPreparationScriptAsync(
                fixture,
                outputRoot,
                TimeSpan.FromSeconds(15));

            AssertPreparationFailedClosed(
                result,
                fixture.DotnetInvocationLog,
                sourceMarker,
                $"an {childName} child junction escaping OutputRoot");
        }
        finally
        {
            if (Directory.Exists(escapedChildRoot))
                Directory.Delete(escapedChildRoot);

            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_RejectsTopLevelHardLinkBeforePublishingOrOverwriting()
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-output-hardlink-guard-{Guid.NewGuid():N}");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);

        var fixture = CreatePreparationScriptFixture(sourceScript, testRoot);
        var outputRoot = Path.Combine(testRoot, "isolated-output");
        var sourceMarker = Path.Combine(
            fixture.ProjectRoot,
            "App",
            "source-marker.txt");
        var linkedDestination = Path.Combine(
            outputRoot,
            "Set-ApiBaseUrl.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceMarker)!);
        Directory.CreateDirectory(outputRoot);
        File.WriteAllText(sourceMarker, "must survive unchanged");
        CreateFileHardLink(linkedDestination, sourceMarker);

        try
        {
            var result = await RunPreparationScriptAsync(
                fixture,
                outputRoot,
                TimeSpan.FromSeconds(15));

            AssertPreparationFailedClosed(
                result,
                fixture.DotnetInvocationLog,
                sourceMarker,
                "a top-level hard link");
            Assert.Equal(
                "must survive unchanged",
                File.ReadAllText(sourceMarker));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_RejectsNestedMobileHardLinkBeforePublishingOrOverwriting()
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-output-mobile-hardlink-guard-{Guid.NewGuid():N}");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);

        var fixture = CreatePreparationScriptFixture(sourceScript, testRoot);
        var outputRoot = Path.Combine(testRoot, "isolated-output");
        var sourceMarker = Path.Combine(
            fixture.ProjectRoot,
            "App",
            "source-marker.txt");
        var linkedDestination = Path.Combine(
            outputRoot,
            "Mobile",
            "candidate.apk");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceMarker)!);
        Directory.CreateDirectory(Path.GetDirectoryName(linkedDestination)!);
        File.WriteAllText(sourceMarker, "must survive unchanged");
        CreateFileHardLink(linkedDestination, sourceMarker);

        try
        {
            var result = await RunPreparationScriptAsync(
                fixture,
                outputRoot,
                TimeSpan.FromSeconds(15));

            AssertPreparationFailedClosed(
                result,
                fixture.DotnetInvocationLog,
                sourceMarker,
                "a nested Mobile hard link");
            Assert.Equal(
                "must survive unchanged",
                File.ReadAllText(sourceMarker));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Theory]
    [InlineData("App")]
    [InlineData("Server")]
    [InlineData("AppData")]
    [InlineData("ServerData")]
    [InlineData("RuntimeLogs")]
    [InlineData("Mobile")]
    public async Task PreparationScript_RejectsNestedReparsePointBeforePublishingOrDeleting(
        string childName)
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-output-nested-junction-guard-{Guid.NewGuid():N}");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);

        var fixture = CreatePreparationScriptFixture(sourceScript, testRoot);
        var outputRoot = Path.Combine(testRoot, "isolated-output");
        var childRoot = Path.Combine(outputRoot, childName);
        var nestedJunctionRoot = Path.Combine(
            childRoot,
            "nested",
            "external-alias");
        var sourceMarker = Path.Combine(
            fixture.ProjectRoot,
            "App",
            "source-marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedJunctionRoot)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceMarker)!);
        File.WriteAllText(sourceMarker, "must survive");
        CreateDirectoryJunction(
            nestedJunctionRoot,
            fixture.ProjectRoot);

        try
        {
            var result = await RunPreparationScriptAsync(
                fixture,
                outputRoot,
                TimeSpan.FromSeconds(15));

            AssertPreparationFailedClosed(
                result,
                fixture.DotnetInvocationLog,
                sourceMarker,
                $"a nested reparse point under {childName}");
        }
        finally
        {
            if (Directory.Exists(nestedJunctionRoot))
                Directory.Delete(nestedJunctionRoot);

            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task StopIsolatedRuntimeProcesses_DoesNotTerminateCurrentProcessOrAncestors()
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-process-safety-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime-root");
        var harnessScript = Path.Combine(outputRoot, "invoke-runtime-cleanup.ps1");
        var wrapperScript = Path.Combine(
            outputRoot,
            "invoke-runtime-cleanup-wrapper.ps1");
        var grandparentScript = Path.Combine(
            outputRoot,
            "invoke-runtime-cleanup-grandparent.ps1");
        var siblingScript = Path.Combine(testRoot, "similar-prefix-sibling.ps1");
        var runtimeVictimScript = Path.Combine(outputRoot, "stale-runtime.ps1");
        var childMarker = Path.Combine(testRoot, "child-survived.txt");
        var parentMarker = Path.Combine(testRoot, "parent-survived.txt");
        var grandparentMarker = Path.Combine(testRoot, "grandparent-survived.txt");
        var siblingReadyMarker = Path.Combine(testRoot, "sibling-ready.txt");
        var runtimeVictimReadyMarker = Path.Combine(
            testRoot,
            "runtime-victim-ready.txt");
        var similarPrefixExecutableRoot = outputRoot + "-old";
        var similarPrefixExecutablePath = Path.Combine(
            similarPrefixExecutableRoot,
            "ping.exe");
        Process? similarPrefixSibling = null;
        Process? similarPrefixExecutable = null;
        Process? runtimeVictim = null;

        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);
        Directory.CreateDirectory(testRoot);
        Directory.CreateDirectory(outputRoot);

        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(
                harnessScript,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$OutputRoot,
                    [Parameter(Mandatory = $true)][string]$ChildMarker
                )

                $ErrorActionPreference = 'Stop'
                Set-StrictMode -Version Latest
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join [Environment]::NewLine)
                }

                $requiredFunctionNames = @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'ConvertTo-NormalizedFullPath',
                    'Get-FinalExistingPath',
                    'Resolve-PhysicalPathIdentity',
                    'Test-PathSameOrDescendant',
                    'Stop-IsolatedRuntimeProcesses'
                )
                foreach ($functionName in $requiredFunctionNames) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }

                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }
                Stop-IsolatedRuntimeProcesses -OutputRoot $OutputRoot
                [System.IO.File]::WriteAllText(
                    $ChildMarker,
                    'child-survived',
                    [System.Text.UTF8Encoding]::new($false))
                """,
                utf8NoBom);
            File.WriteAllText(
                wrapperScript,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$PowerShellPath,
                    [Parameter(Mandatory = $true)][string]$HarnessScript,
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$OutputRoot,
                    [Parameter(Mandatory = $true)][string]$ChildMarker,
                    [Parameter(Mandatory = $true)][string]$ParentMarker
                )

                $ErrorActionPreference = 'Stop'
                $childArguments = @(
                    '-NoProfile',
                    '-NonInteractive',
                    '-ExecutionPolicy',
                    'Bypass',
                    '-File',
                    $HarnessScript,
                    '-SourceScript',
                    $SourceScript,
                    '-OutputRoot',
                    $OutputRoot,
                    '-ChildMarker',
                    $ChildMarker
                )
                & $PowerShellPath @childArguments
                if ($LASTEXITCODE -ne 0) {
                    throw "Cleanup child process failed: exitCode=$LASTEXITCODE"
                }

                [System.IO.File]::WriteAllText(
                    $ParentMarker,
                    'parent-survived',
                    [System.Text.UTF8Encoding]::new($false))
                """,
                utf8NoBom);
            File.WriteAllText(
                grandparentScript,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$PowerShellPath,
                    [Parameter(Mandatory = $true)][string]$WrapperScript,
                    [Parameter(Mandatory = $true)][string]$HarnessScript,
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$OutputRoot,
                    [Parameter(Mandatory = $true)][string]$ChildMarker,
                    [Parameter(Mandatory = $true)][string]$ParentMarker,
                    [Parameter(Mandatory = $true)][string]$GrandparentMarker
                )

                $ErrorActionPreference = 'Stop'
                $parentArguments = @(
                    '-NoProfile',
                    '-NonInteractive',
                    '-ExecutionPolicy',
                    'Bypass',
                    '-File',
                    $WrapperScript,
                    '-PowerShellPath',
                    $PowerShellPath,
                    '-HarnessScript',
                    $HarnessScript,
                    '-SourceScript',
                    $SourceScript,
                    '-OutputRoot',
                    $OutputRoot,
                    '-ChildMarker',
                    $ChildMarker,
                    '-ParentMarker',
                    $ParentMarker
                )
                & $PowerShellPath @parentArguments
                if ($LASTEXITCODE -ne 0) {
                    throw "Cleanup parent process failed: exitCode=$LASTEXITCODE"
                }

                [System.IO.File]::WriteAllText(
                    $GrandparentMarker,
                    'grandparent-survived',
                    [System.Text.UTF8Encoding]::new($false))
                """,
                utf8NoBom);
            File.WriteAllText(
                siblingScript,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$OutputRoot,
                    [Parameter(Mandatory = $true)][string]$LogPath,
                    [Parameter(Mandatory = $true)][string]$ReadyMarker
                )

                if ([string]::IsNullOrWhiteSpace($LogPath)) {
                    throw 'LogPath is required.'
                }
                [System.IO.File]::WriteAllText(
                    $ReadyMarker,
                    'ready',
                    [System.Text.UTF8Encoding]::new($false))
                Start-Sleep -Seconds 120
                """,
                utf8NoBom);
            File.WriteAllText(
                runtimeVictimScript,
                """
                [CmdletBinding()]
                param([Parameter(Mandatory = $true)][string]$ReadyMarker)

                [System.IO.File]::WriteAllText(
                    $ReadyMarker,
                    'ready',
                    [System.Text.UTF8Encoding]::new($false))
                Start-Sleep -Seconds 120
                """,
                utf8NoBom);

            var powerShellPath = ResolveWindowsPowerShellPath();
            similarPrefixSibling = StartPowerShellProcess(
                powerShellPath,
                siblingScript,
                "-OutputRoot",
                outputRoot + "-old",
                "-LogPath",
                Path.Combine(outputRoot, "Server", "server.log"),
                "-ReadyMarker",
                siblingReadyMarker);
            await WaitForFileAsync(siblingReadyMarker, TimeSpan.FromSeconds(5));
            Directory.CreateDirectory(similarPrefixExecutableRoot);
            File.Copy(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "ping.exe"),
                similarPrefixExecutablePath);
            similarPrefixExecutable = Process.Start(
                new ProcessStartInfo
                {
                    FileName = similarPrefixExecutablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList =
                    {
                        "-n",
                        "120",
                        "-w",
                        "1000",
                        "127.0.0.1"
                    }
                })
                ?? throw new InvalidOperationException(
                    "The similar-prefix executable probe did not start.");
            await Task.Delay(200);
            Assert.False(
                similarPrefixExecutable.HasExited,
                "The similar-prefix executable probe exited before cleanup.");
            runtimeVictim = StartPowerShellProcess(
                powerShellPath,
                runtimeVictimScript,
                "-ReadyMarker",
                runtimeVictimReadyMarker);
            await WaitForFileAsync(
                runtimeVictimReadyMarker,
                TimeSpan.FromSeconds(5));
            var result = await RunPowerShellAsync(
                powerShellPath,
                grandparentScript,
                TimeSpan.FromSeconds(20),
                "-PowerShellPath",
                powerShellPath,
                "-WrapperScript",
                wrapperScript,
                "-HarnessScript",
                harnessScript,
                "-SourceScript",
                sourceScript,
                "-OutputRoot",
                outputRoot,
                "-ChildMarker",
                childMarker,
                "-ParentMarker",
                parentMarker,
                "-GrandparentMarker",
                grandparentMarker);

            Assert.True(
                result.ExitCode == 0,
                "The runtime cleanup process did not return normally." +
                Environment.NewLine +
                "STDOUT:" + Environment.NewLine +
                result.Stdout +
                Environment.NewLine +
                "STDERR:" + Environment.NewLine +
                result.Stderr);
            Assert.True(
                File.Exists(childMarker),
                "The cleanup function terminated its own PowerShell process.");
            Assert.True(
                File.Exists(parentMarker),
                "The cleanup function terminated its immediate parent PowerShell process.");
            Assert.True(
                File.Exists(grandparentMarker),
                "The cleanup function terminated a higher ancestor PowerShell process.");
            Assert.False(
                similarPrefixSibling.HasExited,
                "The cleanup function terminated a non-runtime process that only referenced runtime data.");
            Assert.False(
                similarPrefixExecutable.HasExited,
                "The cleanup function crossed the executable-path directory boundary.");
            await runtimeVictim.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(
                runtimeVictim.HasExited,
                "The cleanup function did not terminate a stale runtime entry point.");
        }
        finally
        {
            if (runtimeVictim is not null)
            {
                if (!runtimeVictim.HasExited)
                    runtimeVictim.Kill(entireProcessTree: true);

                try
                {
                    await runtimeVictim.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    // The unique D-drive test root remains isolated even if cleanup is delayed.
                }

                runtimeVictim.Dispose();
            }

            if (similarPrefixExecutable is not null)
            {
                if (!similarPrefixExecutable.HasExited)
                    similarPrefixExecutable.Kill(entireProcessTree: true);

                try
                {
                    await similarPrefixExecutable.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    // The unique D-drive test root remains isolated even if cleanup is delayed.
                }

                similarPrefixExecutable.Dispose();
            }

            if (similarPrefixSibling is not null)
            {
                if (!similarPrefixSibling.HasExited)
                    similarPrefixSibling.Kill(entireProcessTree: true);

                try
                {
                    await similarPrefixSibling.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    // The unique D-drive test root remains isolated even if cleanup is delayed.
                }

                similarPrefixSibling.Dispose();
            }

            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task StopIsolatedRuntimeProcesses_RejectsVolumeRootWithoutEnumeratingProcesses()
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-volume-root-guard-{Guid.NewGuid():N}");
        var probeScript = Path.Combine(testRoot, "invoke-volume-root-guard.ps1");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);
        Directory.CreateDirectory(testRoot);

        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(
                probeScript,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$OutputRoot
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

                $requiredFunctionNames = @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'ConvertTo-NormalizedFullPath',
                    'Get-FinalExistingPath',
                    'Resolve-PhysicalPathIdentity',
                    'Test-PathSameOrDescendant',
                    'Stop-IsolatedRuntimeProcesses'
                )
                foreach ($functionName in $requiredFunctionNames) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }

                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }
                function Get-CimInstance {
                    [CmdletBinding()]
                    param([Parameter(Position = 0)][string]$ClassName)
                    return @()
                }

                Stop-IsolatedRuntimeProcesses -OutputRoot $OutputRoot
                """,
                utf8NoBom);

            var powerShellPath = ResolveWindowsPowerShellPath();
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(testRoot))
                             ?? throw new InvalidOperationException(
                                 "The D-drive test volume root was not resolved.");
            var result = await RunPowerShellAsync(
                powerShellPath,
                probeScript,
                TimeSpan.FromSeconds(10),
                "-SourceScript",
                sourceScript,
                "-OutputRoot",
                volumeRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "OutputRoot must be below the volume root",
                result.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task StopIsolatedRuntimeProcesses_SelectsOnlyRuntimeEntryPoints()
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare entrypoint safety {Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime root");
        var probeScript = Path.Combine(testRoot, "invoke-entrypoint-selection.ps1");
        var outsideRoot = Path.Combine(testRoot, "outside-tools");
        var nestedJunctionRoot = Path.Combine(
            outputRoot,
            "App",
            "nested-junction");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);
        Directory.CreateDirectory(testRoot);

        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Directory.CreateDirectory(Path.Combine(outputRoot, "App"));
            Directory.CreateDirectory(outsideRoot);
            File.WriteAllText(
                Path.Combine(outsideRoot, "inspect.ps1"),
                "Start-Sleep -Seconds 120",
                utf8NoBom);
            CreateDirectoryJunction(nestedJunctionRoot, outsideRoot);
            File.WriteAllText(
                probeScript,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$OutputRoot
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

                $requiredFunctionNames = @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'ConvertTo-NormalizedFullPath',
                    'Get-FinalExistingPath',
                    'Resolve-PhysicalPathIdentity',
                    'Test-PathSameOrDescendant',
                    'Stop-IsolatedRuntimeProcesses'
                )
                foreach ($functionName in $requiredFunctionNames) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }

                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }
                $serverDll = Join-Path $OutputRoot 'Server\GeoraePlan.Server.Api.dll'
                $runAllScript = Join-Path $OutputRoot 'Run-All.ps1'
                $runServerBatch = Join-Path $OutputRoot 'Run-Server.cmd'
                $appExecutable = Join-Path $OutputRoot 'App\GeoraePlan.Desktop.App.exe'
                $outsideRoot = Join-Path (Split-Path $OutputRoot -Parent) 'outside-tools'
                $outsideScript = Join-Path $outsideRoot 'inspect.ps1'
                $outsideDll = Join-Path $outsideRoot 'diagnostic.dll'
                $nestedJunctionScript =
                    Join-Path $OutputRoot 'App\nested-junction\inspect.ps1'
                $runtimeLog = Join-Path $OutputRoot 'Server\server.log'
                $similarExecutable = "${OutputRoot}-old\ping.exe"
                $script:fakeProcesses = @(
                    [pscustomobject]@{
                        ProcessId = 61001
                        ParentProcessId = 0
                        Name = 'dotnet.exe'
                        ExecutablePath = 'C:\Program Files\dotnet\dotnet.exe'
                        CommandLine =
                            '"C:\Program Files\dotnet\dotnet.exe" "' +
                            $serverDll +
                            '" --environment Development'
                    },
                    [pscustomobject]@{
                        ProcessId = 61002
                        ParentProcessId = 0
                        Name = 'powershell.exe'
                        ExecutablePath =
                            'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
                        CommandLine =
                            'powershell -NoProfile -File "' +
                            $runAllScript +
                            '"'
                    },
                    [pscustomobject]@{
                        ProcessId = 61003
                        ParentProcessId = 0
                        Name = 'cmd.exe'
                        ExecutablePath = 'C:\Windows\System32\cmd.exe'
                        CommandLine =
                            'C:\Windows\System32\cmd.exe /d /c ""' +
                            $runServerBatch +
                            '" "'
                    },
                    [pscustomobject]@{
                        ProcessId = 61004
                        ParentProcessId = 0
                        Name = 'GeoraePlan.Desktop.App.exe'
                        ExecutablePath = $appExecutable
                        CommandLine = '"' + $appExecutable + '"'
                    },
                    [pscustomobject]@{
                        ProcessId = 61005
                        ParentProcessId = 0
                        Name = 'powershell.exe'
                        ExecutablePath =
                            'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
                        CommandLine =
                            'powershell -File "' +
                            $outsideScript +
                            '" -LogPath "' +
                            $runtimeLog +
                            '"'
                    },
                    [pscustomobject]@{
                        ProcessId = 61006
                        ParentProcessId = 0
                        Name = 'ping.exe'
                        ExecutablePath = $similarExecutable
                        CommandLine = '"' + $similarExecutable + '" -n 120'
                    },
                    [pscustomobject]@{
                        ProcessId = 61007
                        ParentProcessId = 0
                        Name = 'dotnet.exe'
                        ExecutablePath = 'C:\Program Files\dotnet\dotnet.exe'
                        CommandLine =
                            '"C:\Program Files\dotnet\dotnet.exe" "' +
                            $outsideDll +
                            '" "' +
                            $serverDll +
                            '"'
                    },
                    [pscustomobject]@{
                        ProcessId = 61008
                        ParentProcessId = 0
                        Name = 'powershell.exe'
                        ExecutablePath =
                            'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
                        CommandLine =
                            'powershell -Command "Write-Output ''' +
                            $runtimeLog +
                            '''"'
                    },
                    [pscustomobject]@{
                        ProcessId = 61009
                        ParentProcessId = 0
                        Name = 'GeoraePlan.Desktop.App.exe'
                        ExecutablePath = $appExecutable
                        CommandLine = '"' + $appExecutable + '"'
                    },
                    [pscustomobject]@{
                        ProcessId = 61010
                        ParentProcessId = 0
                        Name = 'powershell.exe'
                        ExecutablePath =
                            'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
                        CommandLine =
                            'powershell -File "' +
                            $nestedJunctionScript +
                            '"'
                    }
                )
                $snapshotStartTime = [DateTime]::Now.AddMinutes(-1)
                foreach ($fakeProcess in $script:fakeProcesses) {
                    $fakeProcess |
                        Add-Member `
                            -MemberType NoteProperty `
                            -Name CreationDate `
                            -Value $snapshotStartTime
                }
                $script:stoppedProcessIds =
                    [Collections.Generic.List[int]]::new()
                $script:fakeLiveProcesses = @{}
                foreach ($fakeProcess in $script:fakeProcesses) {
                    $liveProcess = [pscustomobject]@{
                        Id = [int]$fakeProcess.ProcessId
                        ProcessName = [IO.Path]::GetFileNameWithoutExtension(
                            [string]$fakeProcess.Name)
                        StartTime = $snapshotStartTime
                        HasExited = $false
                    }
                    $liveProcess |
                        Add-Member `
                            -MemberType ScriptMethod `
                            -Name Kill `
                            -Value {
                                $this.HasExited = $true
                                $script:stoppedProcessIds.Add([int]$this.Id)
                            }
                    $liveProcess |
                        Add-Member `
                            -MemberType ScriptMethod `
                            -Name WaitForExit `
                            -Value {
                                param([int]$Milliseconds)
                                return [bool]$this.HasExited
                            }
                    $liveProcess |
                        Add-Member `
                            -MemberType ScriptMethod `
                            -Name Dispose `
                            -Value {}
                    $script:fakeLiveProcesses[[int]$fakeProcess.ProcessId] =
                        $liveProcess
                }
                $script:fakeLiveProcesses[61009].StartTime =
                    $snapshotStartTime.AddMinutes(1)

                function Get-CimInstance {
                    [CmdletBinding()]
                    param([Parameter(Position = 0)][string]$ClassName)
                    return $script:fakeProcesses
                }

                function Get-Process {
                    [CmdletBinding()]
                    param([Parameter(Mandatory = $true)][int]$Id)
                    return $script:fakeLiveProcesses[$Id]
                }

                Stop-IsolatedRuntimeProcesses -OutputRoot $OutputRoot
                ($script:stoppedProcessIds | Sort-Object) -join ','
                """,
                utf8NoBom);

            var powerShellPath = ResolveWindowsPowerShellPath();
            var result = await RunPowerShellAsync(
                powerShellPath,
                probeScript,
                TimeSpan.FromSeconds(10),
                "-SourceScript",
                sourceScript,
                "-OutputRoot",
                outputRoot);

            Assert.True(
                result.ExitCode == 0,
                "The runtime entry-point selection probe failed." +
                Environment.NewLine +
                "STDOUT:" + Environment.NewLine +
                result.Stdout +
                Environment.NewLine +
                "STDERR:" + Environment.NewLine +
                result.Stderr);
            Assert.Equal("61001,61002,61003,61004", result.Stdout.Trim());
        }
        finally
        {
            if (Directory.Exists(nestedJunctionRoot))
                Directory.Delete(nestedJunctionRoot);

            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task StopIsolatedRuntimeProcesses_FailsClosedWhenRuntimeCannotStop()
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"prepare-stop-failure-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime-root");
        var probeScript = Path.Combine(testRoot, "invoke-stop-failure.ps1");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);
        Directory.CreateDirectory(testRoot);

        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(
                probeScript,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$OutputRoot
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

                $requiredFunctionNames = @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'ConvertTo-NormalizedFullPath',
                    'Get-FinalExistingPath',
                    'Resolve-PhysicalPathIdentity',
                    'Test-PathSameOrDescendant',
                    'Stop-IsolatedRuntimeProcesses'
                )
                foreach ($functionName in $requiredFunctionNames) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }

                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }
                $snapshotStartTime = [DateTime]::Now.AddMinutes(-1)
                $runtimeScript = Join-Path $OutputRoot 'stale-runtime.ps1'
                $script:fakeProcess = [pscustomobject]@{
                    ProcessId = 62001
                    ParentProcessId = 0
                    Name = 'powershell.exe'
                    ExecutablePath =
                        'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
                    CommandLine = 'powershell -File "' + $runtimeScript + '"'
                    CreationDate = $snapshotStartTime
                }
                $script:fakeLiveProcess = [pscustomobject]@{
                    Id = 62001
                    ProcessName = 'powershell'
                    StartTime = $snapshotStartTime
                    HasExited = $false
                }
                $script:fakeLiveProcess |
                    Add-Member `
                        -MemberType ScriptMethod `
                        -Name Kill `
                        -Value {
                            throw [UnauthorizedAccessException]::new(
                                'denied by process-safety regression test')
                        }
                $script:fakeLiveProcess |
                    Add-Member `
                        -MemberType ScriptMethod `
                        -Name WaitForExit `
                        -Value {
                            param([int]$Milliseconds)
                            return $false
                        }
                $script:fakeLiveProcess |
                    Add-Member `
                        -MemberType ScriptMethod `
                        -Name Dispose `
                        -Value {}

                function Get-CimInstance {
                    [CmdletBinding()]
                    param([Parameter(Position = 0)][string]$ClassName)
                    return $script:fakeProcess
                }

                function Get-Process {
                    [CmdletBinding()]
                    param([Parameter(Mandatory = $true)][int]$Id)
                    return $script:fakeLiveProcess
                }

                Stop-IsolatedRuntimeProcesses -OutputRoot $OutputRoot
                """,
                utf8NoBom);

            var powerShellPath = ResolveWindowsPowerShellPath();
            var result = await RunPowerShellAsync(
                powerShellPath,
                probeScript,
                TimeSpan.FromSeconds(10),
                "-SourceScript",
                sourceScript,
                "-OutputRoot",
                outputRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "Failed to stop isolated runtime processes",
                result.Stderr,
                StringComparison.Ordinal);
            Assert.Contains("PID 62001", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task GeneratedLaunchers_UseDirectDRuntimeTempAndKillChildrenWhenParentIsTerminated()
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"launcher-lifetime-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        var generationScript = Path.Combine(testRoot, "generate-launchers.ps1");
        var jobProbeScript = Path.Combine(outputRoot, "job-probe.ps1");
        var childScript = Path.Combine(outputRoot, "child.ps1");
        var childPidPath = Path.Combine(outputRoot, "child.pid");
        var environmentPath = Path.Combine(outputRoot, "runtime-temp.txt");
        var probeErrorPath = Path.Combine(outputRoot, "job-probe-error.txt");
        var powerShellPath = ResolveWindowsPowerShellPath();
        Process? parent = null;
        Process? child = null;
        Directory.CreateDirectory(outputRoot);

        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                Write-TestRunScripts `
                    -OutputRoot $OutputRoot `
                    -DefaultBaseUrl 'http://127.0.0.1:19081' `
                    -DotnetExe $PowerShellPath `
                    -CertificationId 'launcher-lifetime-test' `
                    -CertificationMode 'test' `
                    -PasswordResetCount 0
                """,
                utf8NoBom);

            var generationResult = await RunPowerShellAsync(
                powerShellPath,
                generationScript,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                sourceScript,
                "-OutputRoot",
                outputRoot,
                "-PowerShellPath",
                powerShellPath);
            Assert.True(
                generationResult.ExitCode == 0,
                "Launcher generation failed." +
                Environment.NewLine +
                generationResult.Stdout +
                Environment.NewLine +
                generationResult.Stderr);

            var runAllPath = Path.Combine(outputRoot, "Run-All.ps1");
            var runComponentPath =
                Path.Combine(outputRoot, "Run-IsolatedComponent.ps1");
            var runAllSource = File.ReadAllText(runAllPath);
            var runComponentSource = File.ReadAllText(runComponentPath);
            AssertRunAllAutoLoginContract(runAllSource);
            foreach (var launcherSource in new[] { runAllSource, runComponentSource })
            {
                Assert.Contains(
                    "function Initialize-IsolatedRuntimeTempEnvironment",
                    launcherSource,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "'GEORAEPLAN_TEMP_ROOT'",
                    launcherSource,
                    StringComparison.Ordinal);
                Assert.Contains("'TEMP'", launcherSource, StringComparison.Ordinal);
                Assert.Contains("'TMP'", launcherSource, StringComparison.Ordinal);
                Assert.Contains(
                    "Runtime temp root must remain directly below the certified D: runtime.",
                    launcherSource,
                    StringComparison.Ordinal);
                Assert.True(
                    CountOccurrences(
                        launcherSource,
                        "Initialize-IsolatedRuntimeTempEnvironment") >= 2,
                    "The generated launcher defines the runtime temp guard but does not invoke it.");
            }

            Assert.Contains(
                "JobObjectLimitKillOnJobClose = 0x00002000",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "$childProcessJob.AssignProcess($serverProcess)",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "$childProcessJob.AssignProcess($appProcess)",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "$childProcessJob.Dispose()",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "RuntimeLogs",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "-RedirectStandardOutput $StdoutLogPath",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "-RedirectStandardError $StderrLogPath",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "server-{0}-a{1}.stdout.log",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "server-{0}-a{1}.stderr.log",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "Assert-SafeRuntimeLogFilePath",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "$appProcess.WaitForExit(250)",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "$serverProcess.HasExited",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "$consecutiveHealthFailures -ge 3",
                runAllSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "Stop-AndDisposeRuntimeProcess",
                runAllSource,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Wait-Process -Id $appProcess.Id",
                runAllSource,
                StringComparison.Ordinal);

            File.WriteAllText(
                childScript,
                "while ($true) { Start-Sleep -Seconds 1 }",
                utf8NoBom);
            File.WriteAllText(
                jobProbeScript,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$ChildScript,
                    [Parameter(Mandatory = $true)][string]$ChildPidPath,
                    [Parameter(Mandatory = $true)][string]$EnvironmentPath,
                    [Parameter(Mandatory = $true)][string]$ErrorPath,
                    [Parameter(Mandatory = $true)][string]$PowerShellPath
                )

                $ErrorActionPreference = 'Stop'
                trap {
                    [IO.File]::WriteAllText(
                        $ErrorPath,
                        $_.Exception.ToString(),
                        [Text.UTF8Encoding]::new($false))
                    exit 1
                }
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
                    'Assert-RuntimeRootHasNoReparsePoint',
                    'Initialize-RuntimeFinalPathNativeMethods',
                    'Get-RuntimePhysicalDirectoryPath',
                    'Initialize-IsolatedRuntimeTempEnvironment'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                $runtimeTemp =
                    Initialize-IsolatedRuntimeTempEnvironment `
                        -RuntimeRoot (Split-Path -Parent $SourceScript)
                [IO.File]::WriteAllLines(
                    $EnvironmentPath,
                    @(
                        $runtimeTemp,
                        $env:GEORAEPLAN_TEMP_ROOT,
                        $env:TEMP,
                        $env:TMP
                    ),
                    [Text.UTF8Encoding]::new($false))

                $job = [GeoraePlan.Runtime.ChildProcessJob]::new()
                $child = Start-Process `
                    -FilePath $PowerShellPath `
                    -ArgumentList @(
                        '-NoProfile',
                        '-NonInteractive',
                        '-ExecutionPolicy',
                        'Bypass',
                        '-File',
                        ('"{0}"' -f $ChildScript)
                    ) `
                    -WindowStyle Hidden `
                    -PassThru
                try {
                    $job.AssignProcess($child)
                }
                catch {
                    if (-not $child.HasExited) {
                        $child.Kill()
                    }
                    throw
                }
                Set-Content `
                    -LiteralPath $ChildPidPath `
                    -Value ([string]$child.Id) `
                    -Encoding ASCII
                while ($true) {
                    Start-Sleep -Seconds 1
                }
                """,
                utf8NoBom);

            parent = StartPowerShellProcess(
                powerShellPath,
                jobProbeScript,
                "-SourceScript",
                runAllPath,
                "-ChildScript",
                childScript,
                "-ChildPidPath",
                childPidPath,
                "-EnvironmentPath",
                environmentPath,
                "-ErrorPath",
                probeErrorPath,
                "-PowerShellPath",
                powerShellPath);
            var markerDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!File.Exists(childPidPath) &&
                   !parent.HasExited &&
                   DateTime.UtcNow < markerDeadline)
            {
                await Task.Delay(50);
            }
            Assert.True(
                File.Exists(childPidPath),
                "The child PID marker was not written." +
                Environment.NewLine +
                (File.Exists(probeErrorPath)
                    ? File.ReadAllText(probeErrorPath)
                    : $"parentExited={parent.HasExited}"));
            await WaitForFileAsync(environmentPath, TimeSpan.FromSeconds(20));

            var childPid = int.Parse(File.ReadAllText(childPidPath).Trim());
            child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited);

            var environmentValues = File.ReadAllLines(environmentPath);
            Assert.Equal(4, environmentValues.Length);
            var expectedTempRoot = Path.GetFullPath(
                Path.Combine(outputRoot, "Temp"));
            foreach (var value in environmentValues)
            {
                Assert.Equal(expectedTempRoot, Path.GetFullPath(value));
                Assert.Equal(
                    "D:\\",
                    Path.GetPathRoot(Path.GetFullPath(value)),
                    ignoreCase: true);
            }
            Assert.True(Directory.Exists(expectedTempRoot));
            Assert.False(
                (File.GetAttributes(expectedTempRoot) &
                 FileAttributes.ReparsePoint) != 0);

            parent.Kill(entireProcessTree: false);
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(
                child.HasExited,
                "The child survived after the Run-All parent job handle closed.");

            await DeleteDirectoryWithRetriesAsync(expectedTempRoot);
            Assert.False(
                Directory.Exists(expectedTempRoot),
                "The terminated child left runtime temp artifacts behind.");
            var escapedTempTarget = Path.Combine(testRoot, "escaped-temp-target");
            Directory.CreateDirectory(escapedTempTarget);
            CreateDirectoryJunction(expectedTempRoot, escapedTempTarget);
            File.Delete(probeErrorPath);
            var reparseResult = await RunPowerShellAsync(
                powerShellPath,
                jobProbeScript,
                TimeSpan.FromSeconds(20),
                "-SourceScript",
                runAllPath,
                "-ChildScript",
                childScript,
                "-ChildPidPath",
                Path.Combine(outputRoot, "reparse-child.pid"),
                "-EnvironmentPath",
                Path.Combine(outputRoot, "reparse-runtime-temp.txt"),
                "-ErrorPath",
                probeErrorPath,
                "-PowerShellPath",
                powerShellPath);
            Assert.NotEqual(0, reparseResult.ExitCode);
            Assert.True(File.Exists(probeErrorPath));
            Assert.Contains(
                "reparse point",
                File.ReadAllText(probeErrorPath),
                StringComparison.OrdinalIgnoreCase);
            Directory.Delete(expectedTempRoot);
        }
        finally
        {
            if (parent is not null)
            {
                if (!parent.HasExited)
                    parent.Kill(entireProcessTree: true);
                try
                {
                    await parent.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                }
                parent.Dispose();
            }

            if (child is not null)
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
                try
                {
                    await child.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                }
                child.Dispose();
            }

            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public void GeneratedRunAll_UsesValidatedAndroidMetadataSidecarWithoutHardcodedVersion()
    {
        var source = File.ReadAllText(ResolvePreparationScript());

        Assert.Contains(
            "android-package.metadata.json",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$androidMetadata.versionName",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$androidMetadata.sha256",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "version = '0.2.18'",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "minimumSupportedVersion = '0.2.18'",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedRunAll_AdvertisesOnlyInstallableHighestVersionDesktopPackage()
    {
        var sourceScript = ResolvePreparationScript();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"launcher-update-manifest-{Guid.NewGuid():N}");
        var runtimeRoot = Path.Combine(testRoot, "runtime");
        var serverDataRoot = Path.Combine(runtimeRoot, "ServerData");
        var desktopDownloadRoot = Path.Combine(
            serverDataRoot,
            "updates",
            "downloads",
            "desktop");
        var generationScript = Path.Combine(testRoot, "generate-launchers.ps1");
        var manifestHarness = Path.Combine(
            testRoot,
            "initialize-update-manifest.ps1");
        var manifestLog = Path.Combine(testRoot, "manifest.log");
        var powerShellPath = ResolveWindowsPowerShellPath();
        Directory.CreateDirectory(desktopDownloadRoot);

        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                Write-TestRunScripts `
                    -OutputRoot $OutputRoot `
                    -DefaultBaseUrl 'http://127.0.0.1:19081' `
                    -DotnetExe $PowerShellPath `
                    -CertificationId 'launcher-update-manifest-test' `
                    -CertificationMode 'test' `
                    -PasswordResetCount 0
                """,
                utf8NoBom);

            var generationResult = await RunPowerShellAsync(
                powerShellPath,
                generationScript,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                sourceScript,
                "-OutputRoot",
                runtimeRoot,
                "-PowerShellPath",
                powerShellPath);
            Assert.True(
                generationResult.ExitCode == 0,
                "Launcher generation failed." +
                Environment.NewLine +
                generationResult.Stdout +
                Environment.NewLine +
                generationResult.Stderr);

            const string lowerVersion = "1.2.0";
            const string highestVersion = "1.10.0";
            var lowerVersionExecutable =
                CreateVersionedDesktopExecutableFixture(
                    powerShellPath,
                    testRoot,
                    lowerVersion,
                    "lower");
            var highestVersionExecutable =
                CreateVersionedDesktopExecutableFixture(
                    powerShellPath,
                    testRoot,
                    highestVersion,
                    "highest");

            var minimalFakePackagePath = Path.Combine(
                desktopDownloadRoot,
                "tradeplan-pc-installer-v9.0.0.zip");
            var fullPlaceholderPackagePath = Path.Combine(
                desktopDownloadRoot,
                "tradeplan-pc-installer-v8.0.0.zip");
            var versionMismatchPackagePath = Path.Combine(
                desktopDownloadRoot,
                "tradeplan-pc-installer-v7.0.0.zip");
            var missingContractPackagePath = Path.Combine(
                desktopDownloadRoot,
                "tradeplan-pc-installer-v9.0.1.zip");
            var invalidNamePackagePath = Path.Combine(
                desktopDownloadRoot,
                "tradeplan-pc-installer-vtest.zip");
            var aliasPackagePath = Path.Combine(
                desktopDownloadRoot,
                $"tradeplan-pc-installer-v{highestVersion}.zip");
            var oversizedScriptPackagePath = Path.Combine(
                desktopDownloadRoot,
                $"tradeplan-pc-installer-v{lowerVersion}.zip");
            CreateDesktopInstallerZip(
                minimalFakePackagePath,
                includeRequiredInstallContract: true,
                includeRequiredArchiveEntries: false);
            CreateDesktopInstallerZip(
                fullPlaceholderPackagePath,
                includeRequiredInstallContract: true);
            CreateDesktopInstallerZip(
                versionMismatchPackagePath,
                includeRequiredInstallContract: true,
                desktopExecutablePath: highestVersionExecutable,
                updaterExecutablePath: highestVersionExecutable);
            CreateDesktopInstallerZip(
                missingContractPackagePath,
                includeRequiredInstallContract: false);
            CreateDesktopInstallerZip(
                invalidNamePackagePath,
                includeRequiredInstallContract: true);
            CreateDesktopInstallerZip(
                aliasPackagePath,
                includeRequiredInstallContract: true,
                desktopExecutablePath: highestVersionExecutable,
                updaterExecutablePath: highestVersionExecutable,
                addPhysicalAliasEntry: true);
            CreateDesktopInstallerZip(
                oversizedScriptPackagePath,
                includeRequiredInstallContract: true,
                desktopExecutablePath: lowerVersionExecutable,
                updaterExecutablePath: lowerVersionExecutable,
                installScriptPaddingBytes: 1048577);
            File.SetLastWriteTimeUtc(
                minimalFakePackagePath,
                new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(
                missingContractPackagePath,
                new DateTime(2026, 7, 24, 3, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(
                invalidNamePackagePath,
                new DateTime(2026, 7, 24, 4, 0, 0, DateTimeKind.Utc));

            File.WriteAllText(
                manifestHarness,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
                    [Parameter(Mandatory = $true)][string]$ServerDataRoot,
                    [Parameter(Mandatory = $true)][string]$LogPath
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
                    'ConvertTo-ValidatedDesktopArchiveEntryPath',
                    'Read-BoundedDesktopArchiveTextEntry',
                    'Test-DesktopArchivePortableExecutableEntry',
                    'Test-DesktopUpdatePackageContract',
                    'Initialize-TestUpdateManifest'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$functionName function was not found."
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                function Write-Log {
                    param([string]$Message)
                    Add-Content -LiteralPath $LogPath -Value $Message -Encoding UTF8
                }

                Initialize-TestUpdateManifest `
                    -ServerDataRoot $ServerDataRoot `
                    -RuntimeRoot $RuntimeRoot
                """,
                utf8NoBom);

            var untrustedManifestResult = await RunPowerShellAsync(
                powerShellPath,
                manifestHarness,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                Path.Combine(runtimeRoot, "Run-All.ps1"),
                "-RuntimeRoot",
                runtimeRoot,
                "-ServerDataRoot",
                serverDataRoot,
                "-LogPath",
                manifestLog);
            Assert.True(
                untrustedManifestResult.ExitCode == 0,
                "Untrusted-package manifest initialization failed." +
                Environment.NewLine +
                untrustedManifestResult.Stdout +
                Environment.NewLine +
                untrustedManifestResult.Stderr);

            var manifestPath = Path.Combine(
                serverDataRoot,
                "updates",
                "manifest",
                "test.json");
            Assert.True(File.Exists(manifestPath));
            using (var untrustedManifestDocument = JsonDocument.Parse(
                       File.ReadAllText(manifestPath)))
            {
                var untrustedManifest = untrustedManifestDocument.RootElement;
                Assert.Equal(
                    "test",
                    untrustedManifest.GetProperty("channel").GetString());
                Assert.False(
                    untrustedManifest.TryGetProperty("android", out _));
                Assert.False(
                    untrustedManifest.TryGetProperty("desktop", out _));
            }

            File.Delete(minimalFakePackagePath);
            File.Delete(fullPlaceholderPackagePath);
            File.Delete(versionMismatchPackagePath);
            File.Delete(missingContractPackagePath);
            File.Delete(invalidNamePackagePath);
            File.Delete(aliasPackagePath);
            File.Delete(oversizedScriptPackagePath);

            var laterTimestampLowerVersionPackagePath = Path.Combine(
                desktopDownloadRoot,
                $"tradeplan-pc-installer-v{lowerVersion}.zip");
            var highestVersionPackagePath = Path.Combine(
                desktopDownloadRoot,
                $"tradeplan-pc-installer-v{highestVersion}.zip");
            CreateDesktopInstallerZip(
                laterTimestampLowerVersionPackagePath,
                includeRequiredInstallContract: true,
                desktopExecutablePath: lowerVersionExecutable,
                updaterExecutablePath: lowerVersionExecutable);
            CreateDesktopInstallerZip(
                highestVersionPackagePath,
                includeRequiredInstallContract: true,
                desktopExecutablePath: highestVersionExecutable,
                updaterExecutablePath: highestVersionExecutable);
            File.SetLastWriteTimeUtc(
                laterTimestampLowerVersionPackagePath,
                new DateTime(2026, 7, 24, 6, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(
                highestVersionPackagePath,
                new DateTime(2026, 7, 24, 5, 0, 0, DateTimeKind.Utc));

            var manifestResult = await RunPowerShellAsync(
                powerShellPath,
                manifestHarness,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                Path.Combine(runtimeRoot, "Run-All.ps1"),
                "-RuntimeRoot",
                runtimeRoot,
                "-ServerDataRoot",
                serverDataRoot,
                "-LogPath",
                manifestLog);
            Assert.True(
                manifestResult.ExitCode == 0,
                "Valid-package manifest initialization failed." +
                Environment.NewLine +
                manifestResult.Stdout +
                Environment.NewLine +
                manifestResult.Stderr);

            using var manifestDocument = JsonDocument.Parse(
                File.ReadAllText(manifestPath));
            var manifest = manifestDocument.RootElement;
            var desktop = manifest.GetProperty("desktop");
            var selectedVersion = desktop.GetProperty("version").GetString();
            var selectedFileName = desktop.GetProperty("fileName").GetString();
            Assert.Equal(
                highestVersion,
                selectedVersion);
            Assert.Equal(
                highestVersion,
                desktop.GetProperty("minimumSupportedVersion").GetString());
            Assert.Equal(
                Path.GetFileName(highestVersionPackagePath),
                selectedFileName);
            Assert.Equal(
                string.Empty,
                desktop.GetProperty("packageUrl").GetString());
            Assert.Equal(
                new FileInfo(highestVersionPackagePath).Length,
                desktop.GetProperty("fileSize").GetInt64());
            Assert.Equal(
                Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        File.ReadAllBytes(highestVersionPackagePath))),
                desktop.GetProperty("sha256").GetString(),
                ignoreCase: true);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    private static void CreateDesktopInstallerZip(
        string path,
        bool includeRequiredInstallContract,
        bool includeRequiredArchiveEntries = true,
        string? desktopExecutablePath = null,
        string? updaterExecutablePath = null,
        bool addPhysicalAliasEntry = false,
        int installScriptPaddingBytes = 0)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(
            file,
            ZipArchiveMode.Create,
            leaveOpen: false);
        var installEntry = archive.CreateEntry("Install-GeoraePlan.ps1");
        using (var installWriter = new StreamWriter(
                   installEntry.Open(),
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            installWriter.WriteLine("param(");
            if (includeRequiredInstallContract)
            {
                installWriter.WriteLine("    [string]$InstallRoot,");
                installWriter.WriteLine("    [switch]$NoLaunch,");
                installWriter.WriteLine("    [switch]$SuppressUi,");
                installWriter.WriteLine("    [int]$WorkerTimeoutSeconds = 1,");
                installWriter.WriteLine("    [string]$LogPath,");
                installWriter.WriteLine("    [switch]$RecoveryOnly,");
                installWriter.WriteLine("    [switch]$LegacyBridgeCopy,");
                installWriter.WriteLine("    [switch]$UpdaterOwnsInstallRootGate,");
                installWriter.WriteLine(
                    "    [int]$InstallRootGateOwnerProcessId = 0,");
                installWriter.WriteLine(
                    "    [string]$InstallRootGateOwnerProcessPath,");
                installWriter.WriteLine(
                    "    [long]$InstallRootGateOwnerProcessStartTimeUtcTicks = 0");
            }
            else
            {
                installWriter.WriteLine("    [switch]$LegacyInstall");
            }
            installWriter.WriteLine(")");
            if (includeRequiredInstallContract)
            {
                installWriter.WriteLine(
                    "# GEORAEPLAN_INSTALL_SUPERVISOR_CONTRACT_V1");
                installWriter.WriteLine(
                    "# GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1");
            }
            if (installScriptPaddingBytes > 0)
                installWriter.Write(new string('x', installScriptPaddingBytes));
        }

        WriteDesktopInstallerExecutableEntry(
            archive,
            @"App\거래플랜.Desktop.App.exe",
            desktopExecutablePath);
        if (!includeRequiredArchiveEntries)
            return;

        WriteDesktopInstallerExecutableEntry(
            archive,
            @"App\거래플랜.exe",
            desktopExecutablePath);
        WriteDesktopInstallerExecutableEntry(
            archive,
            @"App\Updater\거래플랜.Updater.exe",
            updaterExecutablePath ?? desktopExecutablePath);
        if (addPhysicalAliasEntry)
        {
            WriteDesktopInstallerExecutableEntry(
                archive,
                @"App\.\거래플랜.Desktop.App.exe",
                desktopExecutablePath);
        }

        foreach (var (entryName, content) in new[]
                 {
                     (@"App\appsettings.json", "{}"),
                     (
                         @"App\앱실행.cmd",
                         "@echo off\r\n" +
                         "for %%I in (\"%~dp0*.Desktop.App.exe\") do if exist \"%%~fI\" set \"APP_EXE=%%~fI\"\r\n" +
                         "start \"\" \"%APP_EXE%\""),
                     (
                         @"거래플랜-설치.cmd",
                         "@echo off\r\npowershell -ExecutionPolicy Bypass -File \"%~dp0Install-GeoraePlan.ps1\""),
                     (@"README.txt", "TradePlan desktop installer")
                 })
        {
            WriteDesktopInstallerZipEntry(archive, entryName, content);
        }
    }

    private static void WriteDesktopInstallerExecutableEntry(
        ZipArchive archive,
        string entryName,
        string? executablePath)
    {
        var entry = archive.CreateEntry(entryName);
        using var destination = entry.Open();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            destination.WriteByte(0x4D);
            return;
        }

        using var source = File.OpenRead(executablePath);
        source.CopyTo(destination);
    }

    private static void WriteDesktopInstallerZipEntry(
        ZipArchive archive,
        string entryName,
        string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string CreateVersionedDesktopExecutableFixture(
        string powerShellPath,
        string fixtureRoot,
        string version,
        string tag)
    {
        if (!Version.TryParse(version, out _) ||
            version.Any(character =>
                character is not (>= '0' and <= '9') and not '.'))
        {
            throw new InvalidOperationException(
                "Desktop executable fixture version is not safe.");
        }

        var outputRoot = Path.Combine(fixtureRoot, $"versioned-exe-{tag}");
        Directory.CreateDirectory(outputRoot);
        var executablePath = Path.Combine(
            outputRoot,
            "거래플랜.Desktop.App.exe");
        var compilerScriptPath = Path.Combine(
            outputRoot,
            "create-versioned-desktop-exe.ps1");
        var fileVersion = version + ".0";
        File.WriteAllText(
            compilerScriptPath,
            $$"""
            [CmdletBinding()]
            param([Parameter(Mandatory = $true)][string]$OutputPath)
            $ErrorActionPreference = 'Stop'
            $source = @'
            using System;
            using System.Reflection;
            [assembly: AssemblyTitle("GeoraePlan desktop fixture")]
            [assembly: AssemblyProduct("GeoraePlan desktop fixture")]
            [assembly: AssemblyVersion("{{fileVersion}}")]
            [assembly: AssemblyFileVersion("{{fileVersion}}")]
            [assembly: AssemblyInformationalVersion("{{version}}")]
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

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ConfigureWindowsPowerShellModulePath(startInfo, powerShellPath);
        foreach (var argument in new[]
                 {
                     "-NoProfile",
                     "-ExecutionPolicy",
                     "Bypass",
                     "-File",
                     compilerScriptPath,
                     "-OutputPath",
                     executablePath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Versioned desktop fixture compiler did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(
                (int)TimeSpan.FromSeconds(30).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "Versioned desktop fixture compilation timed out.");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Versioned desktop fixture compilation failed." +
                Environment.NewLine +
                stdout +
                Environment.NewLine +
                stderr);
        }

        var actualVersion =
            FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
        if (!string.Equals(actualVersion, version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Versioned desktop fixture ProductVersion is not exact.");
        }
        return executablePath;
    }

    [Fact]
    public void PreparationScript_SkipDataCopyRetainsExistingAppDataAndUsesOnlyIsolatedCredentialPreflight()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var mainStart = source.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            StringComparison.Ordinal);
        Assert.True(mainStart >= 0, "The preparation entry point was not found.");
        var mainSource = source[mainStart..];

        var earlyRetainedGuardIndex = mainSource.IndexOf(
            "Assert-RetainedIsolatedAppSnapshot -Root $finalIsolatedAppRoot",
            StringComparison.Ordinal);
        var buildEnvironmentIndex = mainSource.IndexOf(
            "Initialize-IsolatedBuildEnvironmentOnD",
            StringComparison.Ordinal);
        var preparationLeaseIndex = mainSource.IndexOf(
            "$preparationLease = [IO.File]::Open(",
            StringComparison.Ordinal);
        var stopRuntimeIndex = mainSource.IndexOf(
            "Stop-IsolatedRuntimeProcesses -OutputRoot $OutputRoot",
            buildEnvironmentIndex,
            StringComparison.Ordinal);
        var postStopSnapshotIndex = mainSource.IndexOf(
            "Get-RetainedIsolatedAppSnapshot -Root $finalIsolatedAppRoot",
            stopRuntimeIndex,
            StringComparison.Ordinal);
        var credentialReadIndex = mainSource.IndexOf(
            "Get-StoredSyncCredentialsFromLocalState `",
            preparationLeaseIndex,
            StringComparison.Ordinal);
        var securePreflightRootIndex = mainSource.IndexOf(
            "New-SecureIsolatedWorkDirectory `",
            preparationLeaseIndex,
            StringComparison.Ordinal);
        var securePreflightCleanupIndex = mainSource.IndexOf(
            "Remove-SecureIsolatedWorkDirectory `",
            securePreflightRootIndex,
            StringComparison.Ordinal);
        var preReplacementVerificationIndex = mainSource.IndexOf(
            "-Context 'before managed component promotion'",
            postStopSnapshotIndex,
            StringComparison.Ordinal);
        var runtimeInvalidationIndex = mainSource.IndexOf(
            "-Reason 'preparation-started'",
            StringComparison.Ordinal);
        var promotionIndex = mainSource.IndexOf(
            "Invoke-IsolatedRuntimeComponentPromotion `",
            StringComparison.Ordinal);
        var dataSnapshotIndex = mainSource.IndexOf(
            "Invoke-TestEnvironmentPreparationFaultPoint -Point 'data:before'",
            StringComparison.Ordinal);
        var nonSkipDataCopyIndex = mainSource.IndexOf(
            "else {",
            dataSnapshotIndex,
            StringComparison.Ordinal);
        var copySnapshotIndex = mainSource.IndexOf(
            "Copy-CurrentAppSnapshot `",
            nonSkipDataCopyIndex,
            StringComparison.Ordinal);
        var retainedVerificationIndex = mainSource.IndexOf(
            "-Context 'after managed component promotion'",
            promotionIndex,
            StringComparison.Ordinal);

        Assert.True(
            earlyRetainedGuardIndex >= 0 &&
            earlyRetainedGuardIndex < buildEnvironmentIndex,
            "SkipDataCopy is not validated before build environment mutation.");
        Assert.True(
            stopRuntimeIndex > buildEnvironmentIndex &&
            preparationLeaseIndex > stopRuntimeIndex &&
            postStopSnapshotIndex > preparationLeaseIndex,
            "Retained AppData is not baselined after the isolated runtime is stopped under the preparation lease.");
        Assert.True(
            credentialReadIndex > preparationLeaseIndex &&
            credentialReadIndex > postStopSnapshotIndex &&
            credentialReadIndex < promotionIndex,
            "Stored credentials are not checked under the preparation lease before component promotion.");
        Assert.True(
            securePreflightRootIndex > postStopSnapshotIndex &&
            securePreflightRootIndex < credentialReadIndex &&
            securePreflightCleanupIndex > credentialReadIndex,
            "Credential preflight does not use a private verified GUID work root.");
        Assert.True(
            runtimeInvalidationIndex >= 0 &&
            runtimeInvalidationIndex < preparationLeaseIndex &&
            preReplacementVerificationIndex < promotionIndex,
            "Runtime certification is not invalidated before preparation exclusion and retained AppData revalidation.");
        Assert.DoesNotContain(
            "-AppRoot $SourceAppRoot",
            mainSource,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(mainSource, "-AppRoot $isolatedAppRoot"));
        Assert.True(
            nonSkipDataCopyIndex > dataSnapshotIndex &&
            copySnapshotIndex > nonSkipDataCopyIndex,
            "Fresh AppData copy is not confined to the staged data-copy branch.");
        Assert.Contains(
            "-ReplaceAppData:(-not $SkipDataCopy)",
            mainSource,
            StringComparison.Ordinal);
        Assert.True(
            retainedVerificationIndex > copySnapshotIndex,
            "The retained AppData snapshot is not reverified during preparation.");

        var readme = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "테스트 시행",
            "README.md"));
        var skipDataSectionStart = readme.IndexOf(
            "### 데이터 복사만 건너뛰고",
            StringComparison.Ordinal);
        var nextSectionStart = readme.IndexOf(
            "### 같은 서버를 두 개의 독립 PC 캐시로",
            skipDataSectionStart,
            StringComparison.Ordinal);
        Assert.True(
            skipDataSectionStart >= 0 &&
            nextSectionStart > skipDataSectionStart,
            "The SkipDataCopy README section was not found.");
        var skipDataSection =
            readme[skipDataSectionStart..nextSectionStart];
        Assert.Contains(
            "-SourceUsersSnapshotPath $snapshotPath",
            skipDataSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "-SourceUsersSnapshotSha256 $snapshotSha256",
            skipDataSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "bare `-SkipDataCopy`",
            skipDataSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "기존 격리 `실행환경\\AppData`",
            skipDataSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "원본 AppData가 아닌 보존된 격리 AppData에서만",
            skipDataSection,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-SkipServerSeed")]
    [InlineData("-AllowFallbackOperationalUsers")]
    [InlineData("-AllowRemoteSourceApi")]
    [InlineData("-ResetUnresolvedUserPasswordsForIsolatedTest")]
    public async Task PreparationScript_ResetAllConflictsFailBeforeOutputOrProcessMutation(
        string conflictingSwitch)
    {
        var outputRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"reset-all-gate-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(outputRoot));

        var result = await RunPowerShellAsync(
            ResolveWindowsPowerShellPath(),
            ResolvePreparationScript(),
            TimeSpan.FromSeconds(15),
            "-ResetAllUserPasswordsForIsolatedTest",
            conflictingSwitch,
            "-SourceUsersSnapshotPath",
            Path.Combine(TestProcessIsolation.TempRoot, "not-read.json"),
            "-SourceUsersSnapshotSha256",
            new string('A', 64),
            "-OutputRoot",
            outputRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(
            Directory.Exists(outputRoot),
            "The rejected reset-all gate created or mutated OutputRoot.");
    }

    [Fact]
    public void PreparationScript_ResetAllStructurallySkipsCredentialReadsAndVerifiesBeforeMutation()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var mainStart = source.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            StringComparison.Ordinal);
        var preflightRead = source.IndexOf(
            "Get-StoredSyncCredentialsFromLocalState `",
            mainStart,
            StringComparison.Ordinal);
        var preflightGate = source.LastIndexOf(
            "-not $ResetAllUserPasswordsForIsolatedTest",
            preflightRead,
            StringComparison.Ordinal);
        var seedFunctionStart = source.IndexOf(
            "function Initialize-IsolatedServerData {",
            StringComparison.Ordinal);
        var seedFunctionEnd = source.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            seedFunctionStart,
            StringComparison.Ordinal);
        var seedFunction = source[seedFunctionStart..seedFunctionEnd];
        var resetEmptyCredentials = seedFunction.IndexOf(
            "$storedCredentials = if (",
            StringComparison.Ordinal);
        var seedCredentialRead = seedFunction.IndexOf(
            "Get-StoredSyncCredentialsFromLocalState `",
            StringComparison.Ordinal);
        var resetVerification = seedFunction.IndexOf(
            "Assert-IsolatedAllUserPasswordResetResult `",
            StringComparison.Ordinal);
        var firstUserMutation = seedFunction.IndexOf(
            "Sync-IsolatedServerUsers `",
            StringComparison.Ordinal);

        Assert.True(
            preflightGate >= 0 && preflightGate < preflightRead,
            "SkipDataCopy credential preflight is not gated by reset-all mode.");
        Assert.True(
            resetEmptyCredentials >= 0 &&
            resetEmptyCredentials < seedCredentialRead,
            "Fresh preparation does not assign an empty credential set before the read branch.");
        Assert.Contains(
            ",@()",
            seedFunction[resetEmptyCredentials..seedCredentialRead],
            StringComparison.Ordinal);
        Assert.True(
            resetVerification > seedCredentialRead &&
            resetVerification < firstUserMutation,
            "Reset counts and flags are not verified before the first user mutation.");
        Assert.Contains(
            "-ResetAllPasswords:$ResetAllUserPasswords",
            seedFunction,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationScript_ProtectedSnapshotResetSkipsStoredCredentialDecryption()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var seedFunctionStart = source.IndexOf(
            "function Initialize-IsolatedServerData {",
            StringComparison.Ordinal);
        var seedFunctionEnd = source.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            seedFunctionStart,
            StringComparison.Ordinal);
        var seedFunction = source[seedFunctionStart..seedFunctionEnd];

        var bypassGate = seedFunction.IndexOf(
            "$bypassStoredCredentialsForProtectedSnapshotReset =",
            StringComparison.Ordinal);
        var credentialRead = seedFunction.IndexOf(
            "Get-StoredSyncCredentialsFromLocalState `",
            StringComparison.Ordinal);
        var passwordResolution = seedFunction.IndexOf(
            "Resolve-IsolatedUserDefinitions `",
            StringComparison.Ordinal);

        Assert.True(
            bypassGate >= 0 &&
            bypassGate < credentialRead &&
            credentialRead < passwordResolution,
            "Protected snapshot password reset does not bypass stored credentials before user resolution.");
        Assert.Contains(
            "$ResetUnresolvedPasswords -and",
            seedFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "$null -ne $SourceUsersSnapshot",
            seedFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "invocationMode = 'explicit-protected-snapshot-reset'",
            seedFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "-ResetUnresolvedPasswords:$ResetUnresolvedPasswords",
            seedFunction,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationScript_RuntimeManifestDigestsUseCrossPowerShellOrdinalOrdering()
    {
        var source = File.ReadAllText(ResolvePreparationScript());

        Assert.True(
            CountOccurrences(
                source,
                "[Array]::Sort($orderedManifestLines, [StringComparer]::Ordinal)") >= 3,
            "Runtime manifest hashes are not ordinally ordered in preparation and launch validation.");
        Assert.Contains(
            "[Array]::Sort($lines, [StringComparer]::Ordinal)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "(($manifestLines | Sort-Object) -join [Environment]::NewLine)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationScript_IsolatedPasswordsMeetTheSixCharacterPolicy()
    {
        var source = File.ReadAllText(ResolvePreparationScript());

        Assert.DoesNotContain("'1234'", source, StringComparison.Ordinal);
        Assert.Contains("'123456'", source, StringComparison.Ordinal);
        Assert.Contains(
            "[string]$_.Password -ceq '123456'",
            source,
            StringComparison.Ordinal);
        var passwordFunctionStart = source.LastIndexOf(
            "function New-LocalTestPassword {",
            StringComparison.Ordinal);
        var passwordFunctionEnd = source.IndexOf(
            "}",
            passwordFunctionStart,
            StringComparison.Ordinal);
        Assert.True(
            passwordFunctionStart >= 0 &&
            passwordFunctionEnd > passwordFunctionStart);
        var passwordFunction = source[
            passwordFunctionStart..(passwordFunctionEnd + 1)];
        Assert.Contains(
            "return '123456'",
            passwordFunction,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationScript_GeneratedRunAllHashesWithoutPowerShellModules()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var runAllStart = source.IndexOf(
            "$runAllPsContent = @'",
            StringComparison.Ordinal);
        var runAllEnd = source.IndexOf(
            "'@",
            runAllStart + 1,
            StringComparison.Ordinal);

        Assert.True(runAllStart >= 0 && runAllEnd > runAllStart);
        var runAll = source[runAllStart..runAllEnd];
        var implementation = runAll.IndexOf(
            "function Get-FileHash {",
            StringComparison.Ordinal);
        var firstUse = runAll.IndexOf(
            "Get-FileHash -LiteralPath",
            implementation + 1,
            StringComparison.Ordinal);

        Assert.True(implementation >= 0 && firstUse > implementation);
        Assert.Contains(
            "[Security.Cryptography.SHA256]::Create()",
            runAll,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileMode]::Open",
            runAll,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileShare]::Read",
            runAll,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparationScript_SkipDataCopyWithoutDatabaseFailsBeforeRuntimeMutation()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-skip-data-missing-db-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        var sourceScript = ResolvePreparationScript();
        RepeatablePreparationFixture? fixture = null;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                sourceScript,
                testRoot);
            var appDataRoot = Path.Combine(outputRoot, "AppData");
            Directory.CreateDirectory(Path.Combine(appDataRoot, "data"));
            File.WriteAllText(
                Path.Combine(
                    appDataRoot,
                    ".georaeplan-isolated-seed-root"),
                appDataRoot,
                new UTF8Encoding(false));

            var appSentinel = Path.Combine(outputRoot, "App", "sentinel.txt");
            var serverSentinel = Path.Combine(
                outputRoot,
                "Server",
                "sentinel.txt");
            var readyMarker = Path.Combine(
                outputRoot,
                ".georaeplan-runtime-ready");
            Directory.CreateDirectory(Path.GetDirectoryName(appSentinel)!);
            Directory.CreateDirectory(Path.GetDirectoryName(serverSentinel)!);
            File.WriteAllText(appSentinel, "existing app");
            File.WriteAllText(serverSentinel, "existing server");
            File.WriteAllText(readyMarker, "existing certification");

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "-SkipDataCopy requires an existing isolated AppData database",
                result.Stdout + Environment.NewLine + result.Stderr,
                StringComparison.Ordinal);
            Assert.Equal("existing app", File.ReadAllText(appSentinel));
            Assert.Equal("existing server", File.ReadAllText(serverSentinel));
            Assert.Equal(
                "existing certification",
                File.ReadAllText(readyMarker));
            if (File.Exists(fixture.DotnetInvocationLog))
            {
                Assert.DoesNotContain(
                    "publish",
                    File.ReadAllText(fixture.DotnetInvocationLog),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Theory]
    [InlineData(
        "marker-mismatch",
        "isolated AppData marker does not match its root")]
    [InlineData("sqlite-wal", "WAL/SHM/journal")]
    public async Task PreparationScript_SkipDataCopyRejectsUnsafeRetainedSnapshotBeforeRuntimeMutation(
        string unsafeState,
        string expectedError)
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-skip-data-unsafe-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        var sourceScript = ResolvePreparationScript();
        RepeatablePreparationFixture? fixture = null;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                sourceScript,
                testRoot);
            var appDataRoot = Path.Combine(outputRoot, "AppData");
            var databasePath = Path.Combine(
                appDataRoot,
                "data",
                "거래플랜.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            File.WriteAllText(databasePath, "retained fixture database");
            File.WriteAllText(
                Path.Combine(
                    appDataRoot,
                    ".georaeplan-isolated-seed-root"),
                unsafeState == "marker-mismatch"
                    ? fixture.SourceAppRoot
                    : appDataRoot,
                new UTF8Encoding(false));
            if (unsafeState == "sqlite-wal")
            {
                File.WriteAllText(
                    databasePath + "-wal",
                    "unsafe sidecar");
            }

            var appSentinel = Path.Combine(outputRoot, "App", "sentinel.txt");
            var serverSentinel = Path.Combine(
                outputRoot,
                "Server",
                "sentinel.txt");
            var readyMarker = Path.Combine(
                outputRoot,
                ".georaeplan-runtime-ready");
            Directory.CreateDirectory(Path.GetDirectoryName(appSentinel)!);
            Directory.CreateDirectory(Path.GetDirectoryName(serverSentinel)!);
            File.WriteAllText(appSentinel, "existing app");
            File.WriteAllText(serverSentinel, "existing server");
            File.WriteAllText(readyMarker, "existing certification");

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                expectedError,
                result.Stdout + Environment.NewLine + result.Stderr,
                StringComparison.Ordinal);
            Assert.Equal("existing app", File.ReadAllText(appSentinel));
            Assert.Equal("existing server", File.ReadAllText(serverSentinel));
            Assert.Equal(
                "existing certification",
                File.ReadAllText(readyMarker));
            if (File.Exists(fixture.DotnetInvocationLog))
            {
                Assert.DoesNotContain(
                    "publish",
                    File.ReadAllText(fixture.DotnetInvocationLog),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_SkipDataCopyRevalidatesPostStopSnapshotBeforeRuntimeReplacement()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-skip-data-post-stop-race-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        RepeatablePreparationFixture? fixture = null;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                ResolvePreparationScript(),
                testRoot);
            var appDataRoot = Path.Combine(outputRoot, "AppData");
            var databasePath = Path.Combine(
                appDataRoot,
                "data",
                "거래플랜.db");
            var attachmentPath = Path.Combine(
                appDataRoot,
                "attachments",
                "retained-sentinel.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(attachmentPath)!);
            File.WriteAllText(databasePath, "retained fixture database");
            File.WriteAllText(attachmentPath, "retained fixture attachment");
            File.WriteAllText(
                Path.Combine(
                    appDataRoot,
                    ".georaeplan-isolated-seed-root"),
                appDataRoot,
                new UTF8Encoding(false));

            var appSentinel = Path.Combine(
                outputRoot,
                "App",
                "sentinel.txt");
            var serverSentinel = Path.Combine(
                outputRoot,
                "Server",
                "sentinel.txt");
            var readyMarker = Path.Combine(
                outputRoot,
                ".georaeplan-runtime-ready");
            Directory.CreateDirectory(Path.GetDirectoryName(appSentinel)!);
            Directory.CreateDirectory(Path.GetDirectoryName(serverSentinel)!);
            File.WriteAllText(appSentinel, "existing app");
            File.WriteAllText(serverSentinel, "existing server");
            File.WriteAllText(readyMarker, "existing certification");

            var copiedSource = File.ReadAllText(fixture.CopiedScript);
            var revalidationContextIndex = copiedSource.IndexOf(
                "-Context 'before managed component promotion'",
                StringComparison.Ordinal);
            var revalidationBlockIndex = copiedSource.LastIndexOf(
                "if ($SkipDataCopy) {",
                revalidationContextIndex,
                StringComparison.Ordinal);
            Assert.True(
                revalidationContextIndex >= 0 &&
                revalidationBlockIndex >= 0,
                "The pre-replacement retained snapshot guard was not found.");
            const string injectPostStopSidecar =
                """
                if ($SkipDataCopy) {
                    [IO.File]::WriteAllText(
                        (Join-Path $finalIsolatedAppRoot 'data\거래플랜.db-wal'),
                        'simulated post-stop sidecar')
                }

                """;
            copiedSource = copiedSource.Insert(
                revalidationBlockIndex,
                injectPostStopSidecar);
            File.WriteAllText(
                fixture.CopiedScript,
                copiedSource,
                new UTF8Encoding(true));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "WAL/SHM/journal",
                result.Stdout + Environment.NewLine + result.Stderr,
                StringComparison.Ordinal);
            Assert.Equal("existing app", File.ReadAllText(appSentinel));
            Assert.Equal("existing server", File.ReadAllText(serverSentinel));
            Assert.False(
                File.Exists(readyMarker),
                "A post-stop AppData mutation left the previous runtime certification usable.");
            Assert.True(
                File.Exists(Path.Combine(
                    outputRoot,
                    ".georaeplan-runtime-invalid")),
                "A post-stop AppData mutation did not leave an explicit launcher block.");
            if (File.Exists(fixture.DotnetInvocationLog))
            {
                Assert.Contains(
                    "publish",
                    File.ReadAllText(fixture.DotnetInvocationLog),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_LockedReadyMarkerLeavesExplicitLauncherBlock()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-locked-ready-marker-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        RepeatablePreparationFixture? fixture = null;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                ResolvePreparationScript(),
                testRoot);
            var appDataRoot = Path.Combine(outputRoot, "AppData");
            var databasePath = Path.Combine(
                appDataRoot,
                "data",
                "거래플랜.db");
            var attachmentPath = Path.Combine(
                appDataRoot,
                "attachments",
                "retained-sentinel.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(attachmentPath)!);
            File.WriteAllText(databasePath, "retained fixture database");
            File.WriteAllText(attachmentPath, "retained fixture attachment");
            File.WriteAllText(
                Path.Combine(
                    appDataRoot,
                    ".georaeplan-isolated-seed-root"),
                appDataRoot,
                new UTF8Encoding(false));

            var firstResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot);
            Assert.True(
                firstResult.ExitCode == 0,
                firstResult.Stdout + Environment.NewLine + firstResult.Stderr);

            var readyMarker = Path.Combine(
                outputRoot,
                ".georaeplan-runtime-ready");
            var invalidMarker = Path.Combine(
                outputRoot,
                ".georaeplan-runtime-invalid");
            var componentLauncher = Path.Combine(
                outputRoot,
                "Run-IsolatedComponent.ps1");
            var appExecutable = Directory
                .GetFiles(
                    Path.Combine(outputRoot, "App"),
                    "*.Desktop.App.exe")
                .Single();
            var serverAssembly = Directory
                .GetFiles(
                    Path.Combine(outputRoot, "Server"),
                    "*.Server.Api.dll")
                .Single();
            var appSha256 = ComputeFileSha256(appExecutable);
            var serverSha256 = ComputeFileSha256(serverAssembly);
            var publishCountBefore = CountOccurrences(
                File.ReadAllText(fixture.DotnetInvocationLog),
                "publish ");

            var copiedSource = File.ReadAllText(fixture.CopiedScript);
            var revalidationContextIndex = copiedSource.IndexOf(
                "-Context 'before managed component promotion'",
                StringComparison.Ordinal);
            var revalidationBlockIndex = copiedSource.LastIndexOf(
                "if ($SkipDataCopy) {",
                revalidationContextIndex,
                StringComparison.Ordinal);
            Assert.True(
                revalidationContextIndex >= 0 &&
                revalidationBlockIndex >= 0);
            copiedSource = copiedSource.Insert(
                revalidationBlockIndex,
                """
                if ($SkipDataCopy) {
                    [IO.File]::WriteAllText(
                        (Join-Path $finalIsolatedAppRoot 'data\거래플랜.db-wal'),
                        'simulated post-stop sidecar')
                }

                """);
            File.WriteAllText(
                fixture.CopiedScript,
                copiedSource,
                new UTF8Encoding(true));

            PowerShellResult secondResult;
            using (File.Open(
                       readyMarker,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                secondResult = await RunPowerShellAsync(
                    ResolveWindowsPowerShellPath(),
                    fixture.InvocationScript,
                    TimeSpan.FromSeconds(60),
                    "-PreparationScript",
                    fixture.CopiedScript,
                    "-ProjectRoot",
                    fixture.ProjectRoot,
                    "-OutputRoot",
                    outputRoot,
                    "-SourceAppRoot",
                    fixture.SourceAppRoot,
                    "-FakeDotnet",
                    fixture.FakeDotnet,
                    "-DotnetInvocationLog",
                    fixture.DotnetInvocationLog,
                    "-SnapshotTempRoot",
                    fixture.SnapshotTempRoot);
            }

            Assert.NotEqual(0, secondResult.ExitCode);
            Assert.Contains(
                "Runtime preparation failed and rollback or private workspace cleanup also failed.",
                secondResult.Stdout + Environment.NewLine + secondResult.Stderr,
                StringComparison.Ordinal);
            Assert.Contains(
                "private stage, backup, and quarantine evidence were retained.",
                secondResult.Stdout + Environment.NewLine + secondResult.Stderr,
                StringComparison.Ordinal);
            Assert.True(File.Exists(readyMarker));
            Assert.True(File.Exists(invalidMarker));
            Assert.Equal(appSha256, ComputeFileSha256(appExecutable));
            Assert.Equal(serverSha256, ComputeFileSha256(serverAssembly));
            Assert.Equal(
                publishCountBefore + 2,
                CountOccurrences(
                    File.ReadAllText(fixture.DotnetInvocationLog),
                    "publish "));

            var launcherResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                componentLauncher,
                TimeSpan.FromSeconds(30),
                "-Mode",
                "Server");
            Assert.NotEqual(0, launcherResult.ExitCode);
            Assert.Contains(
                "explicitly invalidated",
                launcherResult.Stdout + Environment.NewLine + launcherResult.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_DataCopyReplacesOnlyIsolatedAppDataAndPreservesSource()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-data-copy-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        var sourceScript = ResolvePreparationScript();
        RepeatablePreparationFixture? fixture = null;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                sourceScript,
                testRoot);
            var sourceDatabasePath = Path.Combine(
                fixture.SourceAppRoot,
                "data",
                "거래플랜.db");
            var sourceAttachmentPath = Path.Combine(
                fixture.SourceAppRoot,
                "attachments",
                "retained-sentinel.txt");
            Directory.CreateDirectory(
                Path.GetDirectoryName(sourceDatabasePath)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(sourceAttachmentPath)!);
            File.WriteAllText(
                sourceDatabasePath,
                "source fixture database");
            File.WriteAllText(
                sourceAttachmentPath,
                "source fixture attachment");
            var sourceDatabaseSha256 =
                ComputeFileSha256(sourceDatabasePath);
            var sourceAttachmentSha256 =
                ComputeFileSha256(sourceAttachmentPath);

            var staleFilePath = Path.Combine(
                outputRoot,
                "AppData",
                "attachments",
                "stale-isolated-only.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(staleFilePath)!);
            File.WriteAllText(staleFilePath, "stale isolated content");

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot,
                "-CopySourceData");

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "stage_preparation_lease_verified",
                result.Stdout,
                StringComparison.Ordinal);
            var isolatedDatabasePath = Path.Combine(
                outputRoot,
                "AppData",
                "data",
                "거래플랜.db");
            var isolatedAttachmentPath = Path.Combine(
                outputRoot,
                "AppData",
                "attachments",
                "retained-sentinel.txt");
            Assert.Equal(
                sourceDatabaseSha256,
                ComputeFileSha256(sourceDatabasePath));
            Assert.Equal(
                sourceAttachmentSha256,
                ComputeFileSha256(sourceAttachmentPath));
            Assert.Equal(
                sourceDatabaseSha256,
                ComputeFileSha256(isolatedDatabasePath));
            Assert.Equal(
                sourceAttachmentSha256,
                ComputeFileSha256(isolatedAttachmentPath));
            Assert.False(File.Exists(staleFilePath));
            Assert.True(File.Exists(Path.Combine(
                outputRoot,
                ".georaeplan-runtime-ready")));
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_SeedFailureRestoresRuntimeAndRemovesPrivateWorkspace()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-seed-rollback-cleanup-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        RepeatablePreparationFixture? fixture = null;
        try
        {
            fixture = CreateRepeatablePreparationFixture(
                ResolvePreparationScript(),
                testRoot);
            var appDataRoot = Path.Combine(outputRoot, "AppData");
            var databasePath = Path.Combine(
                appDataRoot,
                "data",
                GeoraePlan.Tools.SyncDiag.IsolatedPreparationDatabaseLease
                    .LocalDatabaseFileName);
            var attachmentPath = Path.Combine(
                appDataRoot,
                "attachments",
                "retained-sentinel.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(attachmentPath)!);
            File.WriteAllText(databasePath, "retained fixture database");
            File.WriteAllText(attachmentPath, "retained fixture attachment");
            File.WriteAllText(
                Path.Combine(
                    appDataRoot,
                    ".georaeplan-isolated-seed-root"),
                appDataRoot,
                new UTF8Encoding(false));
            var databaseBefore = File.ReadAllBytes(databasePath);
            var attachmentBefore = File.ReadAllBytes(attachmentPath);

            var copiedSource = File.ReadAllText(fixture.CopiedScript);
            var mainStart = copiedSource.IndexOf(
                "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
                StringComparison.Ordinal);
            Assert.True(mainStart >= 0);
            copiedSource = copiedSource.Insert(
                mainStart,
                "$env:GEORAEPLAN_PREPARATION_FAULT_POINT = 'seed:before'" +
                Environment.NewLine);
            File.WriteAllText(
                fixture.CopiedScript,
                copiedSource,
                new UTF8Encoding(true));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "Deterministic preparation fault: seed:before",
                result.Stdout + Environment.NewLine + result.Stderr,
                StringComparison.Ordinal);
            Assert.Equal(databaseBefore, File.ReadAllBytes(databasePath));
            Assert.Equal(attachmentBefore, File.ReadAllBytes(attachmentPath));
            var privateResidue = Directory
                .EnumerateDirectories(testRoot)
                .Where(path =>
                {
                    var leaf = Path.GetFileName(path);
                    return leaf.StartsWith(
                               ".georaeplan-stage-runtime-",
                               StringComparison.Ordinal) ||
                           leaf.StartsWith(
                               ".georaeplan-backup-runtime-",
                               StringComparison.Ordinal) ||
                           leaf.StartsWith(
                               ".georaeplan-quarantine-runtime-",
                               StringComparison.Ordinal);
                })
                .ToArray();
            Assert.Empty(privateResidue);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_ProtectedSnapshotCredentialPreflightUsesRetainedIsolatedAppData()
    {
        const string credentialSecret = "fixture-stored-secret";
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-isolated-credential-preflight-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        var snapshotRoot = Path.Combine(testRoot, "source-users");
        var snapshotPath = Path.Combine(snapshotRoot, "source-users.json");
        var sourceScript = ResolvePreparationScript();
        RepeatablePreparationFixture? fixture = null;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                sourceScript,
                testRoot);
            var retainedAppDataRoot = Path.Combine(outputRoot, "AppData");
            var retainedDatabasePath = Path.Combine(
                retainedAppDataRoot,
                "data",
                "거래플랜.db");
            var retainedAttachmentPath = Path.Combine(
                retainedAppDataRoot,
                "attachments",
                "retained-sentinel.txt");
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedDatabasePath)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedAttachmentPath)!);
            File.WriteAllText(
                retainedDatabasePath,
                "credential preflight fixture database");
            File.WriteAllText(
                retainedAttachmentPath,
                "credential preflight fixture attachment");
            File.WriteAllText(
                Path.Combine(
                    retainedAppDataRoot,
                    ".georaeplan-isolated-seed-root"),
                retainedAppDataRoot,
                new UTF8Encoding(false));

            Directory.CreateDirectory(snapshotRoot);
            const string canonicalUsers =
                "[{\"username\":\"fixture-admin\",\"role\":\"Admin\"," +
                "\"tenantCode\":\"USENET_GROUP\",\"officeCode\":\"USENET\"," +
                "\"scopeType\":\"Admin\",\"isActive\":true," +
                "\"permissions\":[]}]";
            var snapshotPayload = new
            {
                schemaVersion = 1,
                sourceKind = "georaeplan-user-permission-snapshot-v1",
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                isComplete = true,
                userCount = 1,
                permissionCount = 0,
                scopeCounts = new[]
                {
                    new
                    {
                        tenantCode = "USENET_GROUP",
                        officeCode = "USENET",
                        role = "Admin",
                        scopeType = "Admin",
                        isActive = true,
                        userCount = 1,
                        permissionCount = 0
                    }
                },
                canonicalSha256 = ComputeTextSha256(canonicalUsers),
                users = new[]
                {
                    new
                    {
                        username = "fixture-admin",
                        role = "Admin",
                        tenantCode = "USENET_GROUP",
                        officeCode = "USENET",
                        scopeType = "Admin",
                        isActive = true,
                        permissions = Array.Empty<string>()
                    }
                }
            };
            File.WriteAllText(
                snapshotPath,
                JsonSerializer.Serialize(snapshotPayload),
                new UTF8Encoding(false));
            var snapshotSha256 = ComputeFileSha256(snapshotPath);

            var copiedSource = File.ReadAllText(fixture.CopiedScript);
            const string allowedRootAssignment =
                """
                $sourceUsersSnapshotAllowedRoot = Join-Path `
                    ([IO.Path]::GetPathRoot($ProjectRoot)) `
                    'DevCaches\georaeplan-v1-user-snapshots'
                """;
            Assert.Contains(
                allowedRootAssignment,
                copiedSource,
                StringComparison.Ordinal);
            copiedSource = copiedSource.Replace(
                allowedRootAssignment,
                "$sourceUsersSnapshotAllowedRoot = " +
                "$env:FIXTURE_SOURCE_USERS_SNAPSHOT_ROOT",
                StringComparison.Ordinal);
            copiedSource = copiedSource.Replace(
                "        -RequireProtectedAcl",
                "        -RequireProtectedAcl:$false",
                StringComparison.Ordinal);
            File.WriteAllText(
                fixture.CopiedScript,
                copiedSource,
                new UTF8Encoding(true));

            var sourceSentinel = Path.Combine(
                fixture.SourceAppRoot,
                "source-credential-access-forbidden.txt");
            File.WriteAllText(
                sourceSentinel,
                "SourceAppRoot must not be used for credential preflight.");

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot,
                "-SourceUsersSnapshotPath",
                snapshotPath,
                "-SourceUsersSnapshotSha256",
                snapshotSha256);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.DoesNotContain(
                credentialSecret,
                result.Stdout + result.Stderr,
                StringComparison.Ordinal);
            Assert.Equal(
                "SourceAppRoot must not be used for credential preflight.",
                File.ReadAllText(sourceSentinel));
            Assert.False(File.Exists(Path.Combine(
                retainedAppDataRoot,
                Path.GetFileName(sourceSentinel))));

            var dotnetInvocations = File.ReadAllText(
                fixture.DotnetInvocationLog);
            var credentialReadIndex = dotnetInvocations.IndexOf(
                "stored-credential-envelopes-root=isolated",
                StringComparison.Ordinal);
            var publishIndex = dotnetInvocations.IndexOf(
                "Fixture.Desktop.App.csproj",
                StringComparison.Ordinal);
            Assert.True(
                publishIndex >= 0 &&
                credentialReadIndex > publishIndex,
                "Private stage publish did not precede staged credential preflight.");
            var orderedSource = File.ReadAllText(fixture.CopiedScript);
            var mainStart = orderedSource.IndexOf(
                "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
                StringComparison.Ordinal);
            var stagedCredentialPreflightIndex = orderedSource.IndexOf(
                "$preflightStoredCredentials = @(",
                mainStart,
                StringComparison.Ordinal);
            var finalPromotionIndex = orderedSource.IndexOf(
                "Invoke-IsolatedRuntimeComponentPromotion `",
                stagedCredentialPreflightIndex,
                StringComparison.Ordinal);
            Assert.True(
                mainStart >= 0 &&
                stagedCredentialPreflightIndex > mainStart &&
                finalPromotionIndex > stagedCredentialPreflightIndex,
                "Staged credential preflight did not precede final promotion.");
            Assert.DoesNotContain(
                fixture.SourceAppRoot,
                dotnetInvocations,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_ProtectedSnapshotMissingCredentialsFailsBeforeRuntimeMutation()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-isolated-credential-missing-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        var snapshotRoot = Path.Combine(testRoot, "source-users");
        var snapshotPath = Path.Combine(snapshotRoot, "source-users.json");
        RepeatablePreparationFixture? fixture = null;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                ResolvePreparationScript(),
                testRoot);
            var retainedAppDataRoot = Path.Combine(outputRoot, "AppData");
            var retainedDatabasePath = Path.Combine(
                retainedAppDataRoot,
                "data",
                "거래플랜.db");
            var retainedAttachmentPath = Path.Combine(
                retainedAppDataRoot,
                "attachments",
                "retained-sentinel.txt");
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedDatabasePath)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedAttachmentPath)!);
            File.WriteAllText(
                retainedDatabasePath,
                "missing credential fixture database");
            File.WriteAllText(
                retainedAttachmentPath,
                "missing credential fixture attachment");
            File.WriteAllText(
                Path.Combine(
                    retainedAppDataRoot,
                    ".georaeplan-isolated-seed-root"),
                retainedAppDataRoot,
                new UTF8Encoding(false));
            var retainedDatabaseSha256 =
                ComputeFileSha256(retainedDatabasePath);
            var retainedAttachmentSha256 =
                ComputeFileSha256(retainedAttachmentPath);

            var snapshotSha256 = ConfigureSourceUsersSnapshotFixture(
                fixture,
                snapshotRoot,
                snapshotPath);
            var copiedSource = File.ReadAllText(fixture.CopiedScript);
            const string finalPromotionCall =
                "    Invoke-IsolatedRuntimeComponentPromotion `";
            var finalPromotionCallIndex = copiedSource.IndexOf(
                finalPromotionCall,
                copiedSource.IndexOf(
                    "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
                    StringComparison.Ordinal),
                StringComparison.Ordinal);
            Assert.True(finalPromotionCallIndex >= 0);
            copiedSource = copiedSource.Insert(
                finalPromotionCallIndex,
                "    [IO.File]::AppendAllText(" +
                "$env:FAKE_DOTNET_LOG, " +
                "'final-managed-promotion-reached' + " +
                "[Environment]::NewLine)" +
                Environment.NewLine);
            File.WriteAllText(
                fixture.CopiedScript,
                copiedSource,
                new UTF8Encoding(true));

            var appSentinel = Path.Combine(
                outputRoot,
                "App",
                "sentinel.txt");
            var serverSentinel = Path.Combine(
                outputRoot,
                "Server",
                "sentinel.txt");
            var readyMarker = Path.Combine(
                outputRoot,
                ".georaeplan-runtime-ready");
            Directory.CreateDirectory(Path.GetDirectoryName(appSentinel)!);
            Directory.CreateDirectory(Path.GetDirectoryName(serverSentinel)!);
            File.WriteAllText(appSentinel, "existing app");
            File.WriteAllText(serverSentinel, "existing server");
            File.WriteAllText(readyMarker, "existing certification");
            File.WriteAllText(
                Path.Combine(outputRoot, ".georaeplan-prepare-gate.lock"),
                "existing gate");
            File.WriteAllText(
                Path.Combine(outputRoot, ".georaeplan-prepare.lock"),
                "existing lifetime lease");
            var runtimeBytesBefore =
                CapturePreparedRuntimeExecutionBytes(outputRoot);
            var readyBytesBefore = File.ReadAllBytes(readyMarker);

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot,
                "-SourceUsersSnapshotPath",
                snapshotPath,
                "-SourceUsersSnapshotSha256",
                snapshotSha256,
                "-StoredCredentialsJson",
                "{\"schemaVersion\":1,\"protection\":\"DPAPI-CurrentUser\",\"credentials\":[]}");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "비밀번호",
                result.Stdout + Environment.NewLine + result.Stderr,
                StringComparison.Ordinal);
            Assert.Equal("existing app", File.ReadAllText(appSentinel));
            Assert.Equal("existing server", File.ReadAllText(serverSentinel));
            Assert.True(File.Exists(readyMarker));
            Assert.Equal(readyBytesBefore, File.ReadAllBytes(readyMarker));
            Assert.False(File.Exists(Path.Combine(
                outputRoot,
                ".georaeplan-runtime-invalid")));
            Assert.Equal(
                retainedDatabaseSha256,
                ComputeFileSha256(retainedDatabasePath));
            Assert.Equal(
                retainedAttachmentSha256,
                ComputeFileSha256(retainedAttachmentPath));
            var runtimeBytesAfter =
                CapturePreparedRuntimeExecutionBytes(outputRoot);
            Assert.Equal(runtimeBytesBefore.Count, runtimeBytesAfter.Count);
            foreach (var (relativePath, expectedBytes) in runtimeBytesBefore)
            {
                Assert.True(
                    runtimeBytesAfter.TryGetValue(relativePath, out var actualBytes),
                    $"Restored runtime entry is missing: {relativePath}");
                Assert.Equal(expectedBytes, actualBytes);
            }
            var dotnetInvocations = File.ReadAllText(
                fixture.DotnetInvocationLog);
            Assert.Contains(
                "stored-credential-envelopes-root=isolated",
                dotnetInvocations,
                StringComparison.Ordinal);
            Assert.Equal(
                1,
                CountOccurrences(
                    dotnetInvocations,
                    "Fixture.Desktop.App.csproj"));
            Assert.Equal(
                1,
                CountOccurrences(
                    dotnetInvocations,
                    "Fixture.Server.Api.csproj"));
            var desktopPublishIndex = dotnetInvocations.IndexOf(
                "Fixture.Desktop.App.csproj",
                StringComparison.Ordinal);
            var serverPublishIndex = dotnetInvocations.IndexOf(
                "Fixture.Server.Api.csproj",
                StringComparison.Ordinal);
            var stagedCredentialIndex = dotnetInvocations.IndexOf(
                "stored-credential-envelopes-root=isolated",
                StringComparison.Ordinal);
            Assert.True(
                desktopPublishIndex >= 0 &&
                serverPublishIndex >= 0 &&
                stagedCredentialIndex > desktopPublishIndex &&
                stagedCredentialIndex > serverPublishIndex,
                "Private stage publishes did not precede credential preflight.");
            Assert.DoesNotContain(
                "final-managed-promotion-reached",
                dotnetInvocations,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_BuildFailureLeavesExistingRuntimeByteIdentical()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-build-failure-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        RepeatablePreparationFixture? fixture = null;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                ResolvePreparationScript(),
                testRoot);
            var retainedAppDataRoot = Path.Combine(outputRoot, "AppData");
            var retainedDatabasePath = Path.Combine(
                retainedAppDataRoot,
                "data",
                "거래플랜.db");
            var retainedAttachmentPath = Path.Combine(
                retainedAppDataRoot,
                "attachments",
                "retained-sentinel.txt");
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedDatabasePath)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedAttachmentPath)!);
            File.WriteAllText(
                retainedDatabasePath,
                "retained fixture app database");
            File.WriteAllText(
                retainedAttachmentPath,
                "retained fixture attachment");
            File.WriteAllText(
                Path.Combine(
                    retainedAppDataRoot,
                    ".georaeplan-isolated-seed-root"),
                retainedAppDataRoot,
                new UTF8Encoding(false));

            var initialResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot);
            Assert.True(
                initialResult.ExitCode == 0,
                "Initial fixture preparation failed." +
                Environment.NewLine +
                initialResult.Stdout +
                Environment.NewLine +
                initialResult.Stderr);

            var runtimeBytesBefore = CapturePreparedRuntimeExecutionBytes(
                outputRoot);
            var invocationLogBefore = File.ReadAllText(
                fixture.DotnetInvocationLog);
            var publishCountBefore = CountOccurrences(
                invocationLogBefore,
                "publish ");
            Assert.Equal(0, CountOccurrences(invocationLogBefore, "build "));

            var failedResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot,
                "-RunBuild",
                "-FailBuild");

            Assert.NotEqual(0, failedResult.ExitCode);
            var invocationLogAfter = File.ReadAllText(
                fixture.DotnetInvocationLog);
            Assert.Equal(1, CountOccurrences(invocationLogAfter, "build "));
            Assert.Equal(
                publishCountBefore,
                CountOccurrences(invocationLogAfter, "publish "));
            Assert.False(File.Exists(Path.Combine(
                outputRoot,
                ".georaeplan-runtime-invalid")));

            var runtimeBytesAfter = CapturePreparedRuntimeExecutionBytes(
                outputRoot);
            Assert.Equal(
                runtimeBytesBefore.Keys.OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase),
                runtimeBytesAfter.Keys.OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase));
            foreach (var entry in runtimeBytesBefore)
            {
                Assert.True(
                    runtimeBytesAfter[entry.Key].SequenceEqual(entry.Value),
                    $"Prepared runtime bytes changed after build failure: {entry.Key}");
            }
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("junction")]
    [InlineData("missing-leaf")]
    [InlineData("missing-sentinel")]
    public async Task PreparationScript_UnsafeBuildCacheFailsBeforeRuntimeOrBuild(
        string scenario)
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-build-cache-guard-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        var buildCacheJunction = Path.Combine(testRoot, "build-cache-junction");
        RepeatablePreparationFixture? fixture = null;
        var junctionCreated = false;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                ResolvePreparationScript(),
                testRoot);
            var retainedAppDataRoot = Path.Combine(outputRoot, "AppData");
            var retainedDatabasePath = Path.Combine(
                retainedAppDataRoot,
                "data",
                "거래플랜.db");
            var retainedAttachmentPath = Path.Combine(
                retainedAppDataRoot,
                "attachments",
                "retained-sentinel.txt");
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedDatabasePath)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedAttachmentPath)!);
            File.WriteAllText(
                retainedDatabasePath,
                "retained fixture app database");
            File.WriteAllText(
                retainedAttachmentPath,
                "retained fixture attachment");
            File.WriteAllText(
                Path.Combine(
                    retainedAppDataRoot,
                    ".georaeplan-isolated-seed-root"),
                retainedAppDataRoot,
                new UTF8Encoding(false));

            var initialResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot);
            Assert.True(
                initialResult.ExitCode == 0,
                "Initial fixture preparation failed." +
                Environment.NewLine +
                initialResult.Stdout +
                Environment.NewLine +
                initialResult.Stderr);

            var mobileRoot = Path.Combine(outputRoot, "Mobile");
            Directory.CreateDirectory(mobileRoot);
            File.WriteAllText(
                Path.Combine(mobileRoot, "android-package.metadata.json"),
                "{\"fixture\":true}");
            File.WriteAllText(
                Path.Combine(mobileRoot, "root-sentinel.txt"),
                "mobile root sentinel");
            File.WriteAllText(
                Path.Combine(outputRoot, "root-sentinel.txt"),
                "runtime root sentinel");

            var cacheRoot = outputRoot;
            if (string.Equals(
                    scenario,
                    "junction",
                    StringComparison.Ordinal))
            {
                if (!TryCreateDirectoryJunction(
                        buildCacheJunction,
                        outputRoot,
                        out var junctionFailure))
                {
                    throw Xunit.Sdk.SkipException.ForSkip(junctionFailure);
                }

                junctionCreated = true;
                cacheRoot = buildCacheJunction;
            }
            if (scenario.StartsWith("missing-", StringComparison.Ordinal))
            {
                cacheRoot = Path.Combine(testRoot, "incomplete-build-cache");
                ConfigureRepeatableFixtureBuildCacheRoot(fixture, cacheRoot);
                var tempCachePath = Path.Combine(cacheRoot, "temp");
                if (string.Equals(
                        scenario,
                        "missing-leaf",
                        StringComparison.Ordinal))
                {
                    Directory.Delete(tempCachePath, recursive: true);
                }
                else
                {
                    File.Delete(Path.Combine(
                        tempCachePath,
                        ".georaeplan-build-cache-lease"));
                }
            }
            else
            {
                ConfigureRepeatableFixtureBuildCacheRoot(
                    fixture,
                    cacheRoot,
                    provision: false);
            }

            var runtimeBytesBefore = CapturePreparedRuntimeExecutionBytes(
                outputRoot);
            foreach (var requiredEntry in new[]
                     {
                         "D:AppData",
                         "D:ServerData",
                         "D:Mobile",
                         "F:Set-ApiBaseUrl.ps1",
                         "F:Mobile\\android-package.metadata.json",
                         "F:root-sentinel.txt"
                     })
            {
                Assert.Contains(requiredEntry, runtimeBytesBefore.Keys);
            }
            var invocationLogBefore = File.ReadAllText(
                fixture.DotnetInvocationLog);
            var publishCountBefore = CountOccurrences(
                invocationLogBefore,
                "publish ");

            var failedResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot,
                "-RunBuild",
                "-FailBuild");

            var diagnostic =
                failedResult.Stdout + Environment.NewLine + failedResult.Stderr;
            Assert.NotEqual(0, failedResult.ExitCode);
            Assert.Contains(
                "Unsafe isolated build cache:",
                diagnostic,
                StringComparison.Ordinal);
            var invocationLogAfter = File.ReadAllText(
                fixture.DotnetInvocationLog);
            Assert.Equal(0, CountOccurrences(invocationLogAfter, "build "));
            Assert.Equal(
                publishCountBefore,
                CountOccurrences(invocationLogAfter, "publish "));
            Assert.False(File.Exists(Path.Combine(
                outputRoot,
                ".georaeplan-runtime-invalid")));

            var runtimeBytesAfter = CapturePreparedRuntimeExecutionBytes(
                outputRoot);
            Assert.Equal(
                runtimeBytesBefore.Keys.OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase),
                runtimeBytesAfter.Keys.OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase));
            foreach (var entry in runtimeBytesBefore)
            {
                Assert.True(
                    runtimeBytesAfter[entry.Key].SequenceEqual(entry.Value),
                    $"OutputRoot bytes changed through an unsafe build cache: {entry.Key}");
            }
        }
        finally
        {
            if (junctionCreated && Directory.Exists(buildCacheJunction))
                Directory.Delete(buildCacheJunction, recursive: false);

            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationScript_BuildCacheLeafSwapIsBlockedThroughBuild(
        bool swapBeforeMutationLease)
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-build-cache-swap-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        var buildCacheRoot = Path.Combine(testRoot, "private-build-cache");
        var swapResultPath = Path.Combine(testRoot, "cache-swap-result.txt");
        RepeatablePreparationFixture? fixture = null;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                ResolvePreparationScript(),
                testRoot);
            var retainedAppDataRoot = Path.Combine(outputRoot, "AppData");
            var retainedDatabasePath = Path.Combine(
                retainedAppDataRoot,
                "data",
                "거래플랜.db");
            var retainedAttachmentPath = Path.Combine(
                retainedAppDataRoot,
                "attachments",
                "retained-sentinel.txt");
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedDatabasePath)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedAttachmentPath)!);
            File.WriteAllText(
                retainedDatabasePath,
                "retained fixture app database");
            File.WriteAllText(
                retainedAttachmentPath,
                "retained fixture attachment");
            File.WriteAllText(
                Path.Combine(
                    retainedAppDataRoot,
                    ".georaeplan-isolated-seed-root"),
                retainedAppDataRoot,
                new UTF8Encoding(false));

            var initialResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot);
            Assert.True(
                initialResult.ExitCode == 0,
                "Initial fixture preparation failed." +
                Environment.NewLine +
                initialResult.Stdout +
                Environment.NewLine +
                initialResult.Stderr);
            File.WriteAllText(
                Path.Combine(outputRoot, "root-sentinel.txt"),
                "runtime root sentinel");

            ConfigureRepeatableFixtureBuildCacheRoot(
                fixture,
                buildCacheRoot);
            if (swapBeforeMutationLease)
            {
                InjectRepeatableFixtureBuildCachePreMutationLeaseSwapProbe(
                    fixture,
                    outputRoot,
                    swapResultPath);
            }
            else
            {
                InjectRepeatableFixtureBuildCacheSwapProbe(
                    fixture,
                    outputRoot,
                    swapResultPath);
            }
            var runtimeBytesBefore = CapturePreparedRuntimeExecutionBytes(
                outputRoot);
            var invocationLogBefore = File.ReadAllText(
                fixture.DotnetInvocationLog);
            var publishCountBefore = CountOccurrences(
                invocationLogBefore,
                "publish ");

            var failedResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot,
                "-RunBuild",
                "-FailBuild");

            Assert.True(File.Exists(swapResultPath));
            var swapResult = File.ReadAllText(swapResultPath).Trim();
            if (swapResult.StartsWith(
                    "junction-unavailable|",
                    StringComparison.Ordinal))
            {
                throw Xunit.Sdk.SkipException.ForSkip(
                    "The build-cache junction interleaving fixture is unavailable.");
            }
            if (swapBeforeMutationLease)
            {
                Assert.True(
                    swapResult.StartsWith("swapped|", StringComparison.Ordinal),
                    $"The pre-mutation-lease swap did not run: {swapResult}");
            }
            else
            {
                Assert.True(
                    swapResult.StartsWith("blocked|", StringComparison.Ordinal),
                    $"The initialized cache leaf was replaceable: {swapResult}");
            }
            Assert.NotEqual(0, failedResult.ExitCode);
            var invocationLogAfter = File.ReadAllText(
                fixture.DotnetInvocationLog);
            Assert.Equal(
                swapBeforeMutationLease ? 0 : 1,
                CountOccurrences(invocationLogAfter, "build "));
            Assert.Equal(
                publishCountBefore,
                CountOccurrences(invocationLogAfter, "publish "));
            Assert.False(File.Exists(Path.Combine(
                outputRoot,
                ".georaeplan-runtime-invalid")));

            var runtimeBytesAfter = CapturePreparedRuntimeExecutionBytes(
                outputRoot);
            Assert.Equal(
                runtimeBytesBefore.Keys.OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase),
                runtimeBytesAfter.Keys.OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase));
            foreach (var entry in runtimeBytesBefore)
            {
                Assert.True(
                    runtimeBytesAfter[entry.Key].SequenceEqual(entry.Value),
                    $"OutputRoot bytes changed through a cache leaf swap: {entry.Key}");
            }
        }
        finally
        {
            var tempPath = Path.Combine(buildCacheRoot, "temp");
            if (Directory.Exists(tempPath) &&
                (File.GetAttributes(tempPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(tempPath, recursive: false);
            }

            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task PreparationScript_SkipDataCopyRetainsExistingAppDataAcrossPreparations()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"preparation-no-android-repeat-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        var sourceScript = ResolvePreparationScript();
        RepeatablePreparationFixture? fixture = null;

        try
        {
            fixture = CreateRepeatablePreparationFixture(
                sourceScript,
                testRoot);
            var retainedAppDataRoot = Path.Combine(outputRoot, "AppData");
            var retainedDatabasePath = Path.Combine(
                retainedAppDataRoot,
                "data",
                "거래플랜.db");
            var retainedAttachmentPath = Path.Combine(
                retainedAppDataRoot,
                "attachments",
                "retained-sentinel.txt");
            var retainedMarkerPath = Path.Combine(
                retainedAppDataRoot,
                ".georaeplan-isolated-seed-root");
            var sourceOnlyPath = Path.Combine(
                fixture.SourceAppRoot,
                "source-only.txt");
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedDatabasePath)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(retainedAttachmentPath)!);
            File.WriteAllText(
                retainedDatabasePath,
                "retained fixture app database");
            File.WriteAllText(
                retainedAttachmentPath,
                "retained fixture attachment");
            File.WriteAllText(
                retainedMarkerPath,
                retainedAppDataRoot,
                new UTF8Encoding(false));
            File.WriteAllText(
                sourceOnlyPath,
                "must not be copied by SkipDataCopy");
            var retainedDatabaseSha256 =
                ComputeFileSha256(retainedDatabasePath);
            var retainedAttachmentSha256 =
                ComputeFileSha256(retainedAttachmentPath);

            for (var run = 1; run <= 2; run++)
            {
                var result = await RunPowerShellAsync(
                    ResolveWindowsPowerShellPath(),
                    fixture.InvocationScript,
                    TimeSpan.FromSeconds(60),
                    "-PreparationScript",
                    fixture.CopiedScript,
                    "-ProjectRoot",
                    fixture.ProjectRoot,
                    "-OutputRoot",
                    outputRoot,
                    "-SourceAppRoot",
                    fixture.SourceAppRoot,
                    "-FakeDotnet",
                    fixture.FakeDotnet,
                    "-DotnetInvocationLog",
                    fixture.DotnetInvocationLog,
                    "-SnapshotTempRoot",
                    fixture.SnapshotTempRoot);
                Assert.True(
                    result.ExitCode == 0,
                    $"Preparation run {run} failed." +
                    Environment.NewLine +
                    result.Stdout +
                    Environment.NewLine +
                    result.Stderr);

                var readyMarker = Path.Combine(
                    outputRoot,
                    ".georaeplan-runtime-ready");
                Assert.True(File.Exists(readyMarker));
                var marker = File.ReadAllText(readyMarker);
                Assert.Contains(
                    "android_package_state=absent",
                    marker,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "android_package_file_name=none",
                    marker,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "android_package_sha256=none",
                    marker,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "android_package_metadata_sha256=none",
                    marker,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "source_database_snapshot_mode=" +
                    "retained-existing-isolated-snapshot",
                    marker,
                    StringComparison.Ordinal);
                Assert.Equal(
                    retainedDatabaseSha256,
                    ComputeFileSha256(retainedDatabasePath));
                Assert.Equal(
                    retainedAttachmentSha256,
                    ComputeFileSha256(retainedAttachmentPath));
                Assert.Equal(
                    retainedAppDataRoot,
                    File.ReadAllText(retainedMarkerPath).Trim(),
                    ignoreCase: true);
                Assert.False(File.Exists(Path.Combine(
                    retainedAppDataRoot,
                    Path.GetFileName(sourceOnlyPath))));

                using (File.Open(
                           Path.Combine(
                               outputRoot,
                               ".georaeplan-prepare.lock"),
                           FileMode.Open,
                           FileAccess.ReadWrite,
                           FileShare.None))
                {
                }
                Assert.Empty(Directory.EnumerateDirectories(
                    fixture.SnapshotTempRoot,
                    "georaeplan-android-apk-*"));
                Assert.False(File.Exists(Path.Combine(
                    outputRoot,
                    "Mobile",
                    "android-package.metadata.json")));
            }
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public void PreparationScript_UsesPrivateSameVolumeManagedPromotionTransaction()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var mainStart = source.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            StringComparison.Ordinal);
        Assert.True(mainStart >= 0);
        var main = source[mainStart..];

        var stage = main.IndexOf(
            "New-IsolatedRuntimePromotionWorkspace -OutputRoot $OutputRoot",
            StringComparison.Ordinal);
        var stagedLifetimeLease = main.IndexOf(
            "New-StagedRuntimePreparationLease `",
            stage,
            StringComparison.Ordinal);
        var appPublish = main.IndexOf(
            "'publish', $desktopProject",
            stage,
            StringComparison.Ordinal);
        var serverPublish = main.IndexOf(
            "'publish', $serverProject",
            appPublish,
            StringComparison.Ordinal);
        var gate = main.IndexOf(
            "Enter-PreparationGateLease -Path $preparationGateLeasePath",
            serverPublish,
            StringComparison.Ordinal);
        var invalidLease = main.IndexOf(
            "Enter-RuntimeInvalidationMarkerTransactionState `",
            gate,
            StringComparison.Ordinal);
        var invalid = main.IndexOf(
            "-Reason 'preparation-started'",
            invalidLease,
            StringComparison.Ordinal);
        var stop = main.IndexOf(
            "Stop-IsolatedRuntimeProcesses -OutputRoot $OutputRoot",
            invalid,
            StringComparison.Ordinal);
        var lifetimeLease = main.IndexOf(
            "$preparationLease = [IO.File]::Open(",
            stop,
            StringComparison.Ordinal);
        var data = main.IndexOf(
            "Invoke-TestEnvironmentPreparationFaultPoint -Point 'data:before'",
            lifetimeLease,
            StringComparison.Ordinal);
        var retainedMirror = main.IndexOf(
            "-Source $finalIsolatedAppRoot",
            data,
            StringComparison.Ordinal);
        var retainedExactVerification = main.IndexOf(
            "Assert-StagedRetainedIsolatedAppSnapshotExact `",
            retainedMirror,
            StringComparison.Ordinal);
        var retainedTypedRebase = main.IndexOf(
            "Set-TypedIsolatedAppDataSeedRootMarker `",
            retainedExactVerification,
            StringComparison.Ordinal);
        var retainedTypedServerRebase = main.IndexOf(
            "Set-TypedIsolatedServerRootMarker `",
            retainedTypedRebase,
            StringComparison.Ordinal);
        var seed = main.IndexOf(
            "Invoke-TestEnvironmentPreparationFaultPoint -Point 'seed:before'",
            data,
            StringComparison.Ordinal);
        var stagedLifetimeLeaseRelease = main.IndexOf(
            "Close-StagedRuntimePreparationLease `",
            seed,
            StringComparison.Ordinal);
        var stagedMarkerRebase = main.IndexOf(
            "Convert-StagedRuntimeRootMarkers `",
            stagedLifetimeLeaseRelease,
            StringComparison.Ordinal);
        var promotion = main.IndexOf(
            "Invoke-IsolatedRuntimeComponentPromotion `",
            seed,
            StringComparison.Ordinal);
        var readyPublish = main.IndexOf(
            "Publish-TestFileAtomically `",
            promotion,
            StringComparison.Ordinal);
        var invalidClear = main.IndexOf(
            "$nativeType::DeleteHeldExactSingleLinkRegularFile(",
            readyPublish,
            StringComparison.Ordinal);
        var commit = main.IndexOf(
            "Complete-IsolatedRuntimePromotionTransaction `",
            readyPublish,
            StringComparison.Ordinal);

        Assert.True(
            stage >= 0 &&
            stagedLifetimeLease > stage &&
            appPublish > stagedLifetimeLease &&
            serverPublish > appPublish &&
            gate > serverPublish &&
            invalidLease > gate &&
            invalid > invalidLease &&
            stop > invalid &&
            lifetimeLease > stop &&
            data > lifetimeLease &&
            retainedMirror > data &&
            retainedExactVerification > retainedMirror &&
            retainedTypedRebase > retainedExactVerification &&
            retainedTypedServerRebase > retainedTypedRebase &&
            seed > data &&
            stagedLifetimeLeaseRelease > seed &&
            stagedMarkerRebase > stagedLifetimeLeaseRelease &&
            promotion > seed &&
            readyPublish > promotion &&
            commit > readyPublish &&
            invalidClear > commit,
            "The private stage, exclusion, promotion, and certification order is incomplete.");

        foreach (var requiredComponent in new[]
                 {
                     "@('App', 'App', 'Directory', $true)",
                     "@('Server', 'Server', 'Directory', $true)",
                     "@('AppData', 'AppData', 'Directory', $true)",
                     "@('ServerData', 'ServerData', 'Directory', $true)",
                     "@('Mobile', 'Mobile', 'Directory', $true)",
                     "@('Set-ApiBaseUrl.ps1', 'Set-ApiBaseUrl.ps1', 'File', $true)",
                     "@('Run-App.cmd', 'Run-App.cmd', 'File'",
                     "@('Launch-Test-App.vbs', 'Launch-Test-App.vbs', 'File'",
                     "@('Launcher-README.txt', 'Launcher-README.txt', 'File'",
                     "@('Run-Server.cmd', 'Run-Server.cmd', 'File'",
                     "@('Run-IsolatedComponent.ps1', 'Run-IsolatedComponent.ps1', 'File'",
                     "@('Run-All.ps1', 'Run-All.ps1', 'File'",
                     "@('Run-All.cmd', 'Run-All.cmd', 'File'"
                 })
        {
            Assert.Contains(requiredComponent, source, StringComparison.Ordinal);
        }

        foreach (var faultPoint in new[]
                 {
                     "publish:App",
                     "publish:Server",
                     "stage-component:Mobile",
                     "stage-root-file:Set-ApiBaseUrl.ps1",
                     "data:before",
                     "data:after",
                     "server-data:before",
                     "server-data:after",
                     "seed:before",
                     "seed:after",
                     "ready:write:before",
                     "ready:write:after",
                     "ready:publish:after",
                     "invalid:set:before",
                     "invalid:set:after",
                     "invalid:clear:before"
                 })
        {
            Assert.Contains(
                $"-Point '{faultPoint}'",
                main,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "-ReplaceAppData:(-not $SkipDataCopy)",
            main,
            StringComparison.Ordinal);
        Assert.Contains(
            "ManagedFileManifest = @($managedFileManifest)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "The typed AppData seed-root marker rebase changed another",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "The typed server-root marker rebase was not exact",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.Path]::GetPathRoot($normalizedOutputRoot)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "('.georaeplan-stage-{0}-{1}'",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[IO.Directory]::Move($OutputRoot",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationScript_SkipServerSeedCreatesTypedMarkerBeforeCommonRebase()
    {
        var source = File.ReadAllText(ResolvePreparationScript());
        var mainStart = source.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            StringComparison.Ordinal);
        Assert.True(mainStart >= 0);
        var main = source[mainStart..];

        var serverPublish = main.IndexOf(
            "'publish', $serverProject",
            StringComparison.Ordinal);
        var serverDllGuard = main.IndexOf(
            "if (-not (Test-Path -LiteralPath $serverDll -PathType Leaf))",
            serverPublish,
            StringComparison.Ordinal);
        var skipSeedBranch = main.IndexOf(
            "if ($SkipServerSeed) {",
            serverDllGuard,
            StringComparison.Ordinal);
        var skipSeedMarker = main.IndexOf(
            "-Path (Join-Path $serverOutput '.georaeplan-isolated-server-root')",
            skipSeedBranch,
            StringComparison.Ordinal);
        var skipSeedMarkerContent = main.IndexOf(
            "-Content ([IO.Path]::GetFullPath($serverOutput))",
            skipSeedMarker,
            StringComparison.Ordinal);
        var commonRebase = main.IndexOf(
            "Set-TypedIsolatedServerRootMarker `",
            skipSeedMarkerContent,
            StringComparison.Ordinal);

        Assert.True(
            serverPublish >= 0 &&
            serverDllGuard > serverPublish &&
            skipSeedBranch > serverDllGuard &&
            skipSeedMarker > skipSeedBranch &&
            skipSeedMarkerContent > skipSeedMarker &&
            commonRebase > skipSeedMarkerContent,
            "SkipServerSeed does not create its typed server-root marker before the common rebase.");
    }

    [Fact]
    public async Task RuntimePromotionWorkspace_HoldsPrivateIdentityAndRefusesSwappedCleanup()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"runtime-promotion-workspace-identity-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testRoot, "workspace-harness.ps1");
        Directory.CreateDirectory(testRoot);
        try
        {
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$TestRoot
                )
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $source = Get-Content -LiteralPath $SourceScript -Raw
                $ast = [System.Management.Automation.Language.Parser]::ParseInput(
                    $source,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -gt 0) { throw $parseErrors[0] }
                foreach ($functionName in @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'ConvertTo-NormalizedFullPath',
                    'Get-FinalExistingPath',
                    'Resolve-PhysicalPathIdentity',
                    'New-Utf8NoBomEncoding',
                    'Write-Utf8File',
                    'Invoke-TestEnvironmentPreparationFaultPoint',
                    'New-IsolatedRuntimePromotionWorkspace',
                    'Assert-IsolatedRuntimePromotionWorkspace',
                    'Complete-IsolatedRuntimePromotionTransaction'
                )) {
                    $functionNode = $ast.FindAll({
                        param($candidate)
                        $candidate -is
                            [System.Management.Automation.Language.FunctionDefinitionAst]
                    }, $true) | Where-Object { $_.Name -ceq $functionName } |
                        Select-Object -First 1
                    if ($null -eq $functionNode) {
                        throw "Function not found: $functionName"
                    }
                    Invoke-Expression $functionNode.Extent.Text
                }

                function New-WorkspaceCase {
                    param([string]$Name)
                    $caseRoot = Join-Path $TestRoot $Name
                    New-Item -ItemType Directory -Path $caseRoot -Force |
                        Out-Null
                    $output = Join-Path $caseRoot 'runtime'
                    New-Item -ItemType Directory -Path $output -Force |
                        Out-Null
                    return New-IsolatedRuntimePromotionWorkspace `
                        -OutputRoot $output
                }

                function Assert-RenameBlocked {
                    param([object]$Workspace, [string]$Name)
                    $moved = [string]$Workspace.StageRoot + '.moved-' + $Name
                    $blocked = $false
                    try {
                        [IO.Directory]::Move(
                            [string]$Workspace.StageRoot,
                            $moved)
                    }
                    catch {
                        $blocked = $true
                    }
                    if (-not $blocked) {
                        throw "Private root rename was not blocked: $Name"
                    }
                    Assert-IsolatedRuntimePromotionWorkspace `
                        -Workspace $Workspace
                }

                Initialize-TestEnvironmentFinalPathNativeMethods
                $native =
                    [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
                $parentAttackRoot = Join-Path $TestRoot 'parent-attack'
                $parentAttackOutput = Join-Path $parentAttackRoot 'runtime'
                $parentProtected = Join-Path $TestRoot 'parent-protected'
                New-Item -ItemType Directory -Path $parentAttackOutput -Force |
                    Out-Null
                New-Item -ItemType Directory -Path $parentProtected -Force |
                    Out-Null
                $parentProtectedFile =
                    Join-Path $parentProtected 'protected.bin'
                [IO.File]::WriteAllBytes(
                    $parentProtectedFile,
                    [byte[]](11, 22, 33, 44))
                $parentProtectedBytes =
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($parentProtectedFile))
                $parentProtectedAcl = (Get-Acl $parentProtected).Sddl
                $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = '1'
                $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_SOURCE =
                    $parentAttackRoot
                $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_PROTECTED =
                    $parentProtected
                try {
                    $parentWorkspace =
                        New-IsolatedRuntimePromotionWorkspace `
                            -OutputRoot $parentAttackOutput
                }
                finally {
                    $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_SOURCE = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_PROTECTED = $null
                }
                if (
                    $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_RESULT -cne
                        'blocked' -or
                    $null -eq $parentWorkspace.ParentRootLease -or
                    $parentWorkspace.ParentRootLease.IsClosed -or
                    -not (Test-Path $parentProtectedFile -PathType Leaf) -or
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($parentProtectedFile)) -cne
                        $parentProtectedBytes -or
                    (Get-Acl $parentProtected).Sddl -cne $parentProtectedAcl
                ) {
                    throw 'The held parent did not block the pre-create swap.'
                }
                $env:GEORAEPLAN_PREPARATION_TEST_PARENT_SWAP_RESULT = $null
                Complete-IsolatedRuntimePromotionTransaction `
                    -Transaction ([pscustomobject]@{
                        Workspace = $parentWorkspace
                        Committed = $false
                    })

                foreach ($phase in @('post-create', 'post-check')) {
                    $workspace = New-WorkspaceCase -Name $phase
                    foreach ($rootName in @(
                        'StageRoot', 'BackupRoot', 'QuarantineRoot'
                    )) {
                        $native::AssertPrivateDirectoryAcl(
                            [string]$workspace.$rootName)
                    }
                    if ($phase -ceq 'post-check') {
                        Assert-IsolatedRuntimePromotionWorkspace `
                            -Workspace $workspace
                    }
                    Assert-RenameBlocked -Workspace $workspace -Name $phase
                    $transaction = [pscustomobject]@{
                        Workspace = $workspace
                        Committed = $false
                    }
                    Complete-IsolatedRuntimePromotionTransaction `
                        -Transaction $transaction
                }

                $protected = Join-Path $TestRoot 'protected-tree'
                New-Item -ItemType Directory -Path $protected -Force |
                    Out-Null
                $protectedFile = Join-Path $protected 'protected.bin'
                [IO.File]::WriteAllBytes(
                    $protectedFile,
                    [byte[]](0, 1, 2, 3, 254, 255))
                $protectedBytes =
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($protectedFile))
                $protectedAcl = (Get-Acl -LiteralPath $protected).Sddl

                $swapped = New-WorkspaceCase -Name 'identity-swap-cleanup'
                Assert-IsolatedRuntimePromotionWorkspace -Workspace $swapped
                $swapped.StageSentinelLease.Dispose()
                $swapped.StageRootLease.Dispose()
                $retainedStage = [string]$swapped.StageRoot + '.retained'
                [IO.Directory]::Move(
                    [string]$swapped.StageRoot,
                    $retainedStage)
                $junctionOutput = & cmd.exe /d /c mklink /J `
                    ([string]$swapped.StageRoot) `
                    $protected 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "Unable to create swap junction: $junctionOutput"
                }
                $transaction = [pscustomobject]@{
                    Workspace = $swapped
                    Committed = $false
                }
                $cleanupRejected = $false
                try {
                    Complete-IsolatedRuntimePromotionTransaction `
                        -Transaction $transaction
                }
                catch {
                    $cleanupRejected = $true
                }
                if (-not $cleanupRejected) {
                    throw 'Swapped private cleanup was not rejected.'
                }
                if (
                    -not (Test-Path -LiteralPath $swapped.StageRoot) -or
                    -not (Test-Path -LiteralPath $retainedStage) -or
                    -not (Test-Path -LiteralPath $protectedFile -PathType Leaf) -or
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($protectedFile)) -cne
                        $protectedBytes -or
                    (Get-Acl -LiteralPath $protected).Sddl -cne $protectedAcl
                ) {
                    throw 'Swapped cleanup changed protected bytes, ACL, or evidence.'
                }
                Write-Output 'private_workspace_identity_verified'
                """,
                new UTF8Encoding(true));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(45),
                "-SourceScript",
                ResolvePreparationScript(),
                "-TestRoot",
                testRoot);
            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "private_workspace_identity_verified",
                result.Stdout,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task RuntimePromotionChildAndInvalidHandles_RejectExactNameSubstitution()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"runtime-promotion-child-substitution-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testRoot, "child-harness.ps1");
        Directory.CreateDirectory(testRoot);
        try
        {
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$TestRoot
                )
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $source = Get-Content -LiteralPath $SourceScript -Raw
                $ast = [System.Management.Automation.Language.Parser]::ParseInput(
                    $source,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -gt 0) { throw $parseErrors[0] }
                foreach ($functionName in @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'Move-IsolatedRuntimePromotionPath'
                )) {
                    $node = $ast.FindAll({
                        param($candidate)
                        $candidate -is
                            [System.Management.Automation.Language.FunctionDefinitionAst]
                    }, $true) | Where-Object Name -CEQ $functionName |
                        Select-Object -First 1
                    if ($null -eq $node) { throw "Missing function: $functionName" }
                    Invoke-Expression $node.Extent.Text
                }
                Initialize-TestEnvironmentFinalPathNativeMethods
                $native = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]
                if ($null -eq $native.GetMethod(
                        'WaitForExactPrivatePromotionRootRemoval')) {
                    throw 'Exact private root removal stabilization is missing.'
                }

                $stage = Join-Path $TestRoot 'stage'
                $final = Join-Path $TestRoot 'final'
                $protected = Join-Path $TestRoot 'protected-child.bin'
                New-Item -ItemType Directory -Path $stage, $final -Force |
                    Out-Null
                $sourceFile = Join-Path $stage 'component.bin'
                $destinationFile = Join-Path $final 'component.bin'
                [IO.File]::WriteAllBytes($sourceFile, [byte[]](1, 2, 3, 4))
                [IO.File]::WriteAllBytes($protected, [byte[]](101, 102, 103))
                $protectedBytes = [Convert]::ToBase64String(
                    [IO.File]::ReadAllBytes($protected))
                $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = '1'
                $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_SOURCE = $sourceFile
                $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_PROTECTED = $protected
                try {
                    Move-IsolatedRuntimePromotionPath `
                        -Source $sourceFile `
                        -Destination $destinationFile `
                        -Kind File
                }
                finally {
                    $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_SOURCE = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_PROTECTED = $null
                }
                if (
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_RESULT -cne
                        'blocked' -or
                    -not (Test-Path $destinationFile -PathType Leaf) -or
                    -not (Test-Path $protected -PathType Leaf) -or
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($protected)) -cne $protectedBytes
                ) {
                    throw 'Child substitution was not safely rejected.'
                }
                $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_RESULT = $null

                $cleanupRoot = Join-Path $TestRoot 'cleanup-root'
                $cleanupProtected =
                    Join-Path $TestRoot 'protected-cleanup.bin'
                $native::CreatePrivateDirectory($cleanupRoot)
                $cleanupChild = Join-Path $cleanupRoot 'child.bin'
                [IO.File]::WriteAllBytes(
                    $cleanupChild,
                    [byte[]](31, 32, 33, 34))
                [IO.File]::WriteAllBytes(
                    $cleanupProtected,
                    [byte[]](151, 152, 153))
                $cleanupProtectedBytes = [Convert]::ToBase64String(
                    [IO.File]::ReadAllBytes($cleanupProtected))
                $cleanupRootLease = $native::CreateFileW(
                    $cleanupRoot,
                    ($native::DeleteAccess -bor $native::FileListDirectory -bor
                     $native::FileReadAttributes),
                    ($native::FileShareRead -bor $native::FileShareWrite),
                    [IntPtr]::Zero,
                    $native::OpenExisting,
                    ($native::FileFlagBackupSemantics -bor
                     $native::FileFlagOpenReparsePoint),
                    [IntPtr]::Zero)
                if ($cleanupRootLease.IsInvalid) {
                    throw 'Cleanup root lease open failed.'
                }
                $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = '1'
                $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_SOURCE =
                    $cleanupChild
                $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_PROTECTED =
                    $cleanupProtected
                try {
                    $native::DeletePrivatePromotionTreeAndRoot(
                        $cleanupRootLease,
                        $cleanupRoot)
                }
                finally {
                    $cleanupRootLease.Dispose()
                    $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_SOURCE = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_PROTECTED = $null
                }
                if (
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_RESULT -cne
                        'blocked' -or
                    (Test-Path $cleanupRoot) -or
                    -not (Test-Path $cleanupProtected -PathType Leaf) -or
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($cleanupProtected)) -cne
                        $cleanupProtectedBytes
                ) {
                    throw 'Retained cleanup handles did not block substitution.'
                }
                $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SWAP_RESULT = $null

                $preopenRoot = Join-Path $TestRoot 'preopen-cleanup-root'
                $preopenProtected =
                    Join-Path $TestRoot 'protected-preopen.bin'
                $native::CreatePrivateDirectory($preopenRoot)
                $preopenChild = Join-Path $preopenRoot 'child.bin'
                [IO.File]::WriteAllBytes(
                    $preopenChild,
                    [byte[]](61, 62, 63, 64))
                [IO.File]::WriteAllBytes(
                    $preopenProtected,
                    [byte[]](171, 172, 173))
                $preopenProtectedBytes = [Convert]::ToBase64String(
                    [IO.File]::ReadAllBytes($preopenProtected))
                $preopenRootLease = $native::CreateFileW(
                    $preopenRoot,
                    ($native::DeleteAccess -bor $native::FileListDirectory -bor
                     $native::FileReadAttributes),
                    ($native::FileShareRead -bor $native::FileShareWrite),
                    [IntPtr]::Zero,
                    $native::OpenExisting,
                    ($native::FileFlagBackupSemantics -bor
                     $native::FileFlagOpenReparsePoint),
                    [IntPtr]::Zero)
                $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = '1'
                $env:GEORAEPLAN_PREPARATION_TEST_CHILD_PREOPEN_SWAP_SOURCE =
                    $preopenChild
                $env:GEORAEPLAN_PREPARATION_TEST_CHILD_PREOPEN_SWAP_PROTECTED =
                    $preopenProtected
                $preopenRejected = $false
                try {
                    $native::DeletePrivatePromotionTreeAndRoot(
                        $preopenRootLease,
                        $preopenRoot)
                }
                catch {
                    $preopenRejected = $true
                }
                finally {
                    $preopenRootLease.Dispose()
                    $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_PREOPEN_SWAP_SOURCE =
                        $null
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_PREOPEN_SWAP_PROTECTED =
                        $null
                }
                if (
                    -not $preopenRejected -or
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_PREOPEN_SWAP_RESULT -cne
                        'swapped' -or
                    -not (Test-Path $preopenRoot -PathType Container) -or
                    -not (Test-Path $preopenProtected -PathType Leaf) -or
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($preopenProtected)) -cne
                        $preopenProtectedBytes
                ) {
                    throw 'Pre-open child substitution was not preserved.'
                }

                $singleEntryRoot = Join-Path $TestRoot 'single-entry-root'
                $native::CreatePrivateDirectory($singleEntryRoot)
                [IO.File]::WriteAllBytes(
                    (Join-Path $singleEntryRoot 'first.bin'),
                    [byte[]](81, 82, 83))
                [IO.File]::WriteAllBytes(
                    (Join-Path $singleEntryRoot 'second.bin'),
                    [byte[]](91, 92, 93))
                $singleEntryRootLease = $native::CreateFileW(
                    $singleEntryRoot,
                    ($native::DeleteAccess -bor $native::FileListDirectory -bor
                     $native::FileReadAttributes),
                    ($native::FileShareRead -bor $native::FileShareWrite),
                    [IntPtr]::Zero,
                    $native::OpenExisting,
                    ($native::FileFlagBackupSemantics -bor
                     $native::FileFlagOpenReparsePoint),
                    [IntPtr]::Zero)
                $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = '1'
                $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SECOND_RECORD_RENAME =
                    '1'
                try {
                    $native::DeletePrivatePromotionTreeAndRoot(
                        $singleEntryRootLease,
                        $singleEntryRoot)
                }
                finally {
                    $singleEntryRootLease.Dispose()
                    $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SECOND_RECORD_RENAME =
                        $null
                }
                if (
                    $env:GEORAEPLAN_PREPARATION_TEST_CHILD_SECOND_RECORD_RESULT -cne
                        'blocked' -or
                    (Test-Path $singleEntryRoot)
                ) {
                    throw 'Directory records were not bound one at a time.'
                }

                $transientRoot = Join-Path $TestRoot 'transient-lock-root'
                $native::CreatePrivateDirectory($transientRoot)
                $transientDirectory = Join-Path $transientRoot 'nested'
                New-Item -ItemType Directory -Path $transientDirectory |
                    Out-Null
                $transientChild = Join-Path $transientDirectory 'child.bin'
                [IO.File]::WriteAllBytes(
                    $transientChild,
                    [byte[]](111, 112, 113, 114))
                $holderScript = Join-Path $TestRoot 'hold-private-child.ps1'
                $holderReady = Join-Path $TestRoot 'hold-private-child.ready'
                @'
                param([string]$ChildPath, [string]$ReadyPath)
                $stream = [IO.File]::Open(
                    $ChildPath,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    ([IO.FileShare]::Read -bor
                     [IO.FileShare]::Write -bor
                     [IO.FileShare]::Delete))
                try {
                    [IO.File]::WriteAllText($ReadyPath, 'ready')
                    Start-Sleep -Milliseconds 3000
                }
                finally {
                    $stream.Dispose()
                }
                '@ | Set-Content -LiteralPath $holderScript -Encoding UTF8
                $holder = Start-Process `
                    -FilePath (Join-Path $PSHOME 'powershell.exe') `
                    -ArgumentList @(
                        '-NoLogo',
                        '-NoProfile',
                        '-NonInteractive',
                        '-ExecutionPolicy',
                        'Bypass',
                        '-File',
                        $holderScript,
                        '-ChildPath',
                        $transientChild,
                        '-ReadyPath',
                        $holderReady) `
                    -WindowStyle Hidden `
                    -PassThru
                try {
                    $readyDeadline = [DateTime]::UtcNow.AddSeconds(10)
                    while (
                        -not (Test-Path -LiteralPath $holderReady -PathType Leaf) -and
                        [DateTime]::UtcNow -lt $readyDeadline
                    ) {
                        Start-Sleep -Milliseconds 25
                    }
                    if (-not (Test-Path -LiteralPath $holderReady -PathType Leaf)) {
                        throw 'The transient cleanup holder did not become ready.'
                    }
                    if ($holder.HasExited) {
                        throw 'The transient cleanup holder exited before cleanup.'
                    }
                    $transientRootLease = $native::CreateFileW(
                        $transientRoot,
                        ($native::DeleteAccess -bor $native::FileListDirectory -bor
                         $native::FileReadAttributes),
                        ($native::FileShareRead -bor $native::FileShareWrite),
                        [IntPtr]::Zero,
                        $native::OpenExisting,
                        ($native::FileFlagBackupSemantics -bor
                         $native::FileFlagOpenReparsePoint),
                        [IntPtr]::Zero)
                    $transientRootInformation =
                        $native::GetFileInformation($transientRootLease)
                    try {
                        $native::DeletePrivatePromotionTreeAndRoot(
                            $transientRootLease,
                            $transientRoot)
                    }
                    finally {
                        $transientRootLease.Dispose()
                    }
                    $native::WaitForExactPrivatePromotionRootRemoval(
                        $transientRoot,
                        [uint32]$transientRootInformation.VolumeSerialNumber,
                        [uint32]$transientRootInformation.FileIndexHigh,
                        [uint32]$transientRootInformation.FileIndexLow,
                        10000)
                }
                finally {
                    if (-not $holder.HasExited) {
                        [void]$holder.WaitForExit(10000)
                    }
                    $holder.Dispose()
                }
                if (Test-Path -LiteralPath $transientRoot) {
                    throw 'Transient private workspace cleanup was not retried.'
                }

                $invalid = Join-Path $TestRoot 'runtime-invalid'
                $protectedInvalid = Join-Path $TestRoot 'protected-invalid.bin'
                [IO.File]::WriteAllBytes($invalid, [byte[]](9, 8, 7))
                [IO.File]::WriteAllBytes(
                    $protectedInvalid,
                    [byte[]](201, 202, 203))
                $protectedInvalidBytes = [Convert]::ToBase64String(
                    [IO.File]::ReadAllBytes($protectedInvalid))
                $invalidLease = $native::CreateFileW(
                    $invalid,
                    ($native::DeleteAccess -bor $native::FileReadAttributes),
                    ($native::FileShareRead -bor $native::FileShareWrite),
                    [IntPtr]::Zero,
                    $native::OpenExisting,
                    $native::FileFlagOpenReparsePoint,
                    [IntPtr]::Zero)
                if ($invalidLease.IsInvalid) { throw 'Invalid lease open failed.' }
                $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = '1'
                $env:GEORAEPLAN_PREPARATION_TEST_INVALID_SWAP_SOURCE = $invalid
                $env:GEORAEPLAN_PREPARATION_TEST_INVALID_SWAP_PROTECTED =
                    $protectedInvalid
                try {
                    $native::DeleteHeldExactSingleLinkRegularFile(
                        $invalidLease,
                        $invalid)
                }
                finally {
                    $invalidLease.Dispose()
                    $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_INVALID_SWAP_SOURCE = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_INVALID_SWAP_PROTECTED = $null
                }
                if (
                    $env:GEORAEPLAN_PREPARATION_TEST_INVALID_SWAP_RESULT -cne
                        'blocked' -or
                    (Test-Path $invalid) -or
                    -not (Test-Path $protectedInvalid -PathType Leaf) -or
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($protectedInvalid)) -cne
                        $protectedInvalidBytes
                ) {
                    throw 'Invalid marker substitution was not safely rejected.'
                }
                Write-Output 'child_and_invalid_substitution_verified'
                """,
                new UTF8Encoding(true));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(40),
                "-SourceScript",
                ResolvePreparationScript(),
                "-TestRoot",
                testRoot);
            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "child_and_invalid_substitution_verified",
                result.Stdout,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task RuntimeMarkerAndSentinelCreation_AcquireExactHandleBeforeFirstWrite()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"runtime-prehandle-create-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testRoot, "prehandle-harness.ps1");
        Directory.CreateDirectory(testRoot);
        try
        {
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$TestRoot
                )
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $source = Get-Content -LiteralPath $SourceScript -Raw
                $ast = [System.Management.Automation.Language.Parser]::ParseInput(
                    $source,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -gt 0) { throw $parseErrors[0] }
                $node = $ast.FindAll({
                    param($candidate)
                    $candidate -is
                        [System.Management.Automation.Language.FunctionDefinitionAst]
                }, $true) | Where-Object {
                    $_.Name -ceq
                        'Initialize-TestEnvironmentFinalPathNativeMethods'
                } |
                    Select-Object -First 1
                Invoke-Expression $node.Extent.Text
                Initialize-TestEnvironmentFinalPathNativeMethods
                $native = [GeoraePlan.TestEnvironment.FinalPathNativeMethods]

                $runtimeRoot = Join-Path $TestRoot 'runtime'
                New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
                $runtimeLease = $native::CreateFileW(
                    $runtimeRoot,
                    ($native::FileListDirectory -bor
                     $native::FileReadAttributes),
                    ($native::FileShareRead -bor $native::FileShareWrite),
                    [IntPtr]::Zero,
                    $native::OpenExisting,
                    ($native::FileFlagBackupSemantics -bor
                     $native::FileFlagOpenReparsePoint),
                    [IntPtr]::Zero)
                if ($runtimeLease.IsInvalid) { throw 'Runtime lease open failed.' }
                $invalid = Join-Path $runtimeRoot '.georaeplan-runtime-invalid'
                $protectedInvalid = Join-Path $TestRoot 'protected-invalid.bin'
                $oldInvalidBytes = [byte[]](1, 3, 5, 7, 9)
                $protectedInvalidBytes = [byte[]](201, 202, 203, 204)
                [IO.File]::WriteAllBytes($invalid, $oldInvalidBytes)
                [IO.File]::WriteAllBytes(
                    $protectedInvalid,
                    $protectedInvalidBytes)
                $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = '1'
                $env:GEORAEPLAN_PREPARATION_TEST_INVALID_PRELEASE_SWAP_SOURCE =
                    $invalid
                $env:GEORAEPLAN_PREPARATION_TEST_INVALID_PRELEASE_SWAP_PROTECTED =
                    $protectedInvalid
                $priorExists = $false
                [byte[]]$priorBytes = $null
                try {
                    $invalidLease =
                        $native::OpenOrCreateHeldRuntimeInvalidMarker(
                            $runtimeLease,
                            $runtimeRoot,
                            '.georaeplan-runtime-invalid',
                            [byte[]](99, 98, 97),
                            [ref]$priorExists,
                            [ref]$priorBytes)
                }
                finally {
                    $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_INVALID_PRELEASE_SWAP_SOURCE =
                        $null
                    $env:GEORAEPLAN_PREPARATION_TEST_INVALID_PRELEASE_SWAP_PROTECTED =
                        $null
                }
                try {
                    if (
                        $env:GEORAEPLAN_PREPARATION_TEST_INVALID_PRELEASE_SWAP_RESULT -cne
                            'blocked' -or
                        -not $priorExists -or
                        [Convert]::ToBase64String($priorBytes) -cne
                            [Convert]::ToBase64String($oldInvalidBytes) -or
                        [Convert]::ToBase64String(
                            [IO.File]::ReadAllBytes($protectedInvalid)) -cne
                            [Convert]::ToBase64String($protectedInvalidBytes)
                    ) {
                        throw 'Invalid pre-lease substitution was not blocked.'
                    }
                }
                finally {
                    $invalidLease.Dispose()
                    $runtimeLease.Dispose()
                }
                if (
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($invalid)) -cne
                        [Convert]::ToBase64String($oldInvalidBytes)
                ) {
                    throw 'The held invalid marker bytes changed.'
                }

                $newRuntimeRoot = Join-Path $TestRoot 'new-runtime'
                New-Item -ItemType Directory -Path $newRuntimeRoot | Out-Null
                $newRuntimeLease = $native::CreateFileW(
                    $newRuntimeRoot,
                    ($native::FileListDirectory -bor
                     $native::FileReadAttributes),
                    ($native::FileShareRead -bor $native::FileShareWrite),
                    [IntPtr]::Zero,
                    $native::OpenExisting,
                    ($native::FileFlagBackupSemantics -bor
                     $native::FileFlagOpenReparsePoint),
                    [IntPtr]::Zero)
                $newMarkerBytes = [byte[]](71, 72, 73, 74)
                $newPriorExists = $true
                [byte[]]$newPriorBytes = [byte[]](255)
                $newInvalidLease =
                    $native::OpenOrCreateHeldRuntimeInvalidMarker(
                        $newRuntimeLease,
                        $newRuntimeRoot,
                        '.georaeplan-runtime-invalid',
                        $newMarkerBytes,
                        [ref]$newPriorExists,
                        [ref]$newPriorBytes)
                $newInvalid =
                    Join-Path $newRuntimeRoot '.georaeplan-runtime-invalid'
                try {
                    if (
                        $newPriorExists -or
                        $null -ne $newPriorBytes -or
                        [IO.Path]::GetFullPath(
                            $native::GetFinalPath($newInvalidLease)) -cne
                            [IO.Path]::GetFullPath($newInvalid)
                    ) {
                        throw 'Absent invalid marker was not created by handle.'
                    }
                }
                finally {
                    $newInvalidLease.Dispose()
                    $newRuntimeLease.Dispose()
                }
                if (
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($newInvalid)) -cne
                        [Convert]::ToBase64String($newMarkerBytes)
                ) {
                    throw 'Created invalid marker bytes are not durable.'
                }

                $privateRoot = Join-Path $TestRoot 'private-root'
                $native::CreatePrivateDirectory($privateRoot)
                $privateLease = $native::CreateFileW(
                    $privateRoot,
                    ($native::DeleteAccess -bor $native::FileListDirectory -bor
                     $native::FileReadAttributes),
                    ($native::FileShareRead -bor $native::FileShareWrite),
                    [IntPtr]::Zero,
                    $native::OpenExisting,
                    ($native::FileFlagBackupSemantics -bor
                     $native::FileFlagOpenReparsePoint),
                    [IntPtr]::Zero)
                $sentinel = Join-Path $privateRoot 'predictable.sentinel'
                $protectedSentinel = Join-Path $TestRoot 'protected-sentinel.bin'
                $protectedSentinelBytes = [byte[]](41, 42, 43, 44)
                [IO.File]::WriteAllBytes(
                    $protectedSentinel,
                    $protectedSentinelBytes)
                $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = '1'
                $env:GEORAEPLAN_PREPARATION_TEST_SENTINEL_PRECREATE_SOURCE =
                    $sentinel
                $env:GEORAEPLAN_PREPARATION_TEST_SENTINEL_PRECREATE_PROTECTED =
                    $protectedSentinel
                $rejected = $false
                try {
                    $sentinelLease = $native::CreateNewHeldFileUnderDirectory(
                        $privateLease,
                        $privateRoot,
                        'predictable.sentinel',
                        [byte[]](10, 20, 30),
                        'SENTINEL_PRECREATE')
                }
                catch {
                    $rejected = $true
                }
                finally {
                    $privateLease.Dispose()
                    $env:GEORAEPLAN_PREPARATION_ENABLE_UNSAFE_TEST_HOOKS = $null
                    $env:GEORAEPLAN_PREPARATION_TEST_SENTINEL_PRECREATE_SOURCE =
                        $null
                    $env:GEORAEPLAN_PREPARATION_TEST_SENTINEL_PRECREATE_PROTECTED =
                        $null
                }
                if (
                    -not $rejected -or
                    $env:GEORAEPLAN_PREPARATION_TEST_SENTINEL_PRECREATE_RESULT -cne
                        'hardlinked' -or
                    -not (Test-Path $sentinel -PathType Leaf) -or
                    [Convert]::ToBase64String(
                        [IO.File]::ReadAllBytes($protectedSentinel)) -cne
                        [Convert]::ToBase64String($protectedSentinelBytes) -or
                    $native::GetLinkCount($protectedSentinel) -ne 2
                ) {
                    throw 'Sentinel pre-create hardlink was not preserved.'
                }
                Write-Output 'prehandle_creation_verified'
                """,
                new UTF8Encoding(true));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(40),
                "-SourceScript",
                ResolvePreparationScript(),
                "-TestRoot",
                testRoot);
            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "prehandle_creation_verified",
                result.Stdout,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task InvalidClearFailure_AfterCommitKeepsNewRuntimeBlockedWithoutRollback()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"runtime-promotion-invalid-clear-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "runtime");
        RepeatablePreparationFixture? fixture = null;
        try
        {
            fixture = CreateRepeatablePreparationFixture(
                ResolvePreparationScript(),
                testRoot);
            var appDataRoot = Path.Combine(outputRoot, "AppData");
            var databasePath = Path.Combine(
                appDataRoot,
                "data",
                "거래플랜.db");
            var attachmentPath = Path.Combine(
                appDataRoot,
                "attachments",
                "retained-sentinel.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(attachmentPath)!);
            File.WriteAllText(databasePath, "retained fixture database");
            File.WriteAllText(attachmentPath, "retained fixture attachment");
            File.WriteAllText(
                Path.Combine(
                    appDataRoot,
                    ".georaeplan-isolated-seed-root"),
                appDataRoot,
                new UTF8Encoding(false));

            var firstResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot);
            Assert.True(
                firstResult.ExitCode == 0,
                firstResult.Stdout + Environment.NewLine + firstResult.Stderr);

            var readyMarker = Path.Combine(
                outputRoot,
                ".georaeplan-runtime-ready");
            var invalidMarker = Path.Combine(
                outputRoot,
                ".georaeplan-runtime-invalid");
            var componentLauncher = Path.Combine(
                outputRoot,
                "Run-IsolatedComponent.ps1");
            var oldOnlyPath = Path.Combine(
                outputRoot,
                "App",
                "old-only.bin");
            File.WriteAllBytes(oldOnlyPath, new byte[] { 1, 3, 5, 7 });
            var readyBefore = File.ReadAllBytes(readyMarker);

            var copiedSource = File.ReadAllText(fixture.CopiedScript);
            var mainStart = copiedSource.IndexOf(
                "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
                StringComparison.Ordinal);
            Assert.True(mainStart >= 0);
            copiedSource = copiedSource.Insert(
                mainStart,
                "$env:GEORAEPLAN_PREPARATION_FAULT_POINT = " +
                "'invalid:clear:before'" + Environment.NewLine +
                "$env:GEORAEPLAN_PREPARATION_ROLLBACK_FAULT_POINT = " +
                "'rollback:App'" + Environment.NewLine +
                "function Write-RuntimePromotionRollbackEvidence { " +
                "throw 'ENOSPC: injected invalid-evidence full volume' }" +
                Environment.NewLine);
            File.WriteAllText(
                fixture.CopiedScript,
                copiedSource,
                new UTF8Encoding(true));

            var secondResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                fixture.InvocationScript,
                TimeSpan.FromSeconds(60),
                "-PreparationScript",
                fixture.CopiedScript,
                "-ProjectRoot",
                fixture.ProjectRoot,
                "-OutputRoot",
                outputRoot,
                "-SourceAppRoot",
                fixture.SourceAppRoot,
                "-FakeDotnet",
                fixture.FakeDotnet,
                "-DotnetInvocationLog",
                fixture.DotnetInvocationLog,
                "-SnapshotTempRoot",
                fixture.SnapshotTempRoot);

            Assert.NotEqual(0, secondResult.ExitCode);
            var secondDiagnostics =
                secondResult.Stdout + Environment.NewLine + secondResult.Stderr;
            Assert.True(
                secondDiagnostics.Contains(
                    "runtime remains blocked",
                    StringComparison.OrdinalIgnoreCase),
                secondDiagnostics);
            Assert.DoesNotContain(
                "rollback or private workspace cleanup also failed",
                secondDiagnostics,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(oldOnlyPath));
            Assert.True(File.Exists(invalidMarker));
            Assert.True(File.Exists(readyMarker));
            Assert.NotEqual(readyBefore, File.ReadAllBytes(readyMarker));

            var launcherResult = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                componentLauncher,
                TimeSpan.FromSeconds(30),
                "-Mode",
                "Server");
            Assert.NotEqual(0, launcherResult.ExitCode);
            Assert.Contains(
                "explicitly invalidated",
                launcherResult.Stdout + Environment.NewLine + launcherResult.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    [Fact]
    public async Task RuntimePromotionFaults_RestoreExactTreeOrFailClosedWithEvidence()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"runtime-promotion-transaction-{Guid.NewGuid():N}");
        var harnessPath = Path.Combine(testRoot, "promotion-harness.ps1");
        Directory.CreateDirectory(testRoot);
        try
        {
            File.WriteAllText(
                harnessPath,
                """
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$TestRoot
                )
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $source = Get-Content -LiteralPath $SourceScript -Raw
                $ast = [System.Management.Automation.Language.Parser]::ParseInput(
                    $source,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -gt 0) { throw $parseErrors[0] }
                $functionNames = @(
                    'Initialize-TestEnvironmentFinalPathNativeMethods',
                    'New-Utf8NoBomEncoding',
                    'New-Utf8BomEncoding',
                    'Write-Utf8File',
                    'Set-RuntimeInvalidationMarker',
                    'Invoke-TestEnvironmentPreparationFaultPoint',
                    'Get-IsolatedRuntimeManagedComponents',
                    'Get-RuntimeMarkerByteSnapshot',
                    'Restore-RuntimeMarkerByteSnapshot',
                    'New-RuntimePreparationTransaction',
                    'Move-IsolatedRuntimePromotionPath',
                    'Invoke-IsolatedRuntimeComponentPromotion',
                    'Write-RuntimePromotionRollbackEvidence',
                    'Set-RuntimePromotionRollbackFailedClosed',
                    'Restore-IsolatedRuntimePromotionTransaction'
                )
                foreach ($functionName in $functionNames) {
                    $functionNode = $ast.FindAll({
                        param($candidate)
                        $candidate -is
                            [System.Management.Automation.Language.FunctionDefinitionAst]
                    }, $true) | Where-Object { $_.Name -ceq $functionName } |
                        Select-Object -First 1
                    if ($null -eq $functionNode) {
                        throw "Function not found: $functionName"
                    }
                    Invoke-Expression $functionNode.Extent.Text
                }

                function Get-OutputTreeBytes {
                    param([string]$Root)
                    return @(
                        Get-ChildItem -LiteralPath $Root -Recurse -Force |
                            Sort-Object FullName |
                            ForEach-Object {
                                $relative = $_.FullName.Substring($Root.Length).
                                    TrimStart([char[]]@('\', '/'))
                                if ($_.PSIsContainer) {
                                    'D|' + $relative
                                }
                                else {
                                    'F|' + $relative + '|' +
                                        [Convert]::ToBase64String(
                                            [IO.File]::ReadAllBytes($_.FullName))
                                }
                            }
                    ) -join "`n"
                }

                function New-TransactionCase {
                    param([string]$Name)
                    $caseRoot = Join-Path $TestRoot $Name
                    if (Test-Path -LiteralPath $caseRoot) {
                        Remove-Item -LiteralPath $caseRoot -Recurse -Force
                    }
                    $output = Join-Path $caseRoot 'runtime'
                    $stage = Join-Path $caseRoot 'stage'
                    $backup = Join-Path $caseRoot 'backup'
                    $quarantine = Join-Path $caseRoot 'quarantine'
                    foreach ($path in @($output, $stage, $backup, $quarantine)) {
                        New-Item -ItemType Directory -Path $path -Force | Out-Null
                    }
                    foreach ($preservedName in @(
                        'RuntimeLogs', 'Temp', 'MultiPC', 'arbitrary-top-level'
                    )) {
                        $preservedRoot = Join-Path $output $preservedName
                        New-Item -ItemType Directory -Path $preservedRoot |
                            Out-Null
                        [IO.File]::WriteAllBytes(
                            (Join-Path $preservedRoot 'preserved.bin'),
                            [byte[]](0, 1, 2, 3, 255))
                    }
                    [IO.File]::WriteAllBytes(
                        (Join-Path $output '.georaeplan-prepare.lock'),
                        [byte[]](9, 8, 7))
                    [IO.File]::WriteAllBytes(
                        (Join-Path $output '.georaeplan-prepare-gate.lock'),
                        [byte[]](6, 5, 4))
                    $components = @(
                        Get-IsolatedRuntimeManagedComponents `
                            -OutputRoot $output `
                            -StageRoot $stage `
                            -BackupRoot $backup `
                            -ReplaceAppData `
                            -RequireLaunchers
                    )
                    foreach ($component in $components) {
                        if ($component.Kind -ceq 'Directory') {
                            New-Item -ItemType Directory `
                                -Path $component.FinalPath -Force | Out-Null
                            New-Item -ItemType Directory `
                                -Path $component.StagePath -Force | Out-Null
                            [IO.File]::WriteAllBytes(
                                (Join-Path $component.FinalPath 'payload.bin'),
                                [Text.Encoding]::UTF8.GetBytes(
                                    'old-' + $component.Name))
                            [IO.File]::WriteAllBytes(
                                (Join-Path $component.StagePath 'payload.bin'),
                                [Text.Encoding]::UTF8.GetBytes(
                                    'new-' + $component.Name))
                        }
                        else {
                            [IO.File]::WriteAllBytes(
                                $component.FinalPath,
                                [Text.Encoding]::UTF8.GetBytes(
                                    'old-' + $component.Name))
                            [IO.File]::WriteAllBytes(
                                $component.StagePath,
                                [Text.Encoding]::UTF8.GetBytes(
                                    'new-' + $component.Name))
                        }
                    }
                    $ready = Join-Path $output '.georaeplan-runtime-ready'
                    $invalid = Join-Path $output '.georaeplan-runtime-invalid'
                    [IO.File]::WriteAllBytes($ready, [byte[]](239, 187, 191, 1, 2))
                    [IO.File]::WriteAllBytes($invalid, [byte[]](0, 255, 4, 8))
                    $readySnapshot = Get-RuntimeMarkerByteSnapshot -Path $ready
                    $invalidSnapshot = Get-RuntimeMarkerByteSnapshot -Path $invalid
                    $workspace = [pscustomobject]@{
                        TransactionId = [Guid]::NewGuid().ToString('N')
                        OutputRoot = $output
                        StageRoot = $stage
                        BackupRoot = $backup
                        QuarantineRoot = $quarantine
                    }
                    return [pscustomobject]@{
                        Output = $output
                        Stage = $stage
                        Backup = $backup
                        Quarantine = $quarantine
                        Ready = $ready
                        Invalid = $invalid
                        Before = (Get-OutputTreeBytes -Root $output)
                        Transaction = (
                            New-RuntimePreparationTransaction `
                                -Workspace $workspace `
                                -Components $components `
                                -ReadyMarkerSnapshot $readySnapshot `
                                -InvalidMarkerSnapshot $invalidSnapshot)
                    }
                }

                try {
                    $probe = New-TransactionCase -Name 'component-names'
                    foreach ($component in @($probe.Transaction.Components)) {
                        $case = New-TransactionCase `
                            -Name ('ordinary-' + $component.Name.Replace('.', '-'))
                        [IO.File]::WriteAllText($case.Invalid, 'preparation-invalid')
                        $prefix = if ($component.Kind -ceq 'File') {
                            'root-file'
                        }
                        else {
                            'component'
                        }
                        $env:GEORAEPLAN_PREPARATION_FAULT_POINT =
                            $prefix + ':' + $component.Name
                        $faultObserved = $false
                        try {
                            Invoke-IsolatedRuntimeComponentPromotion `
                                -Transaction $case.Transaction
                        }
                        catch {
                            $faultObserved = $true
                        }
                        finally {
                            $env:GEORAEPLAN_PREPARATION_FAULT_POINT = $null
                        }
                        if (-not $faultObserved) {
                            throw "Promotion fault was not observed: $($component.Name)"
                        }
                        Restore-IsolatedRuntimePromotionTransaction `
                            -Transaction $case.Transaction
                        if ((Get-OutputTreeBytes -Root $case.Output) -cne $case.Before) {
                            throw "Output tree was not restored: $($component.Name)"
                        }
                        foreach ($evidenceRoot in @(
                            $case.Stage, $case.Backup, $case.Quarantine
                        )) {
                            if (-not (Test-Path -LiteralPath $evidenceRoot)) {
                                throw "Failure evidence root was removed: $evidenceRoot"
                            }
                        }
                    }

                    foreach ($phase in @('data', 'seed')) {
                        $case = New-TransactionCase -Name ('before-promotion-' + $phase)
                        [IO.File]::WriteAllText($case.Invalid, 'preparation-invalid')
                        Restore-IsolatedRuntimePromotionTransaction `
                            -Transaction $case.Transaction
                        if ((Get-OutputTreeBytes -Root $case.Output) -cne $case.Before) {
                            throw "Pre-promotion failure was not restored: $phase"
                        }
                    }

                    foreach ($phase in @('ready', 'invalid')) {
                        $case = New-TransactionCase -Name ('after-promotion-' + $phase)
                        [IO.File]::WriteAllText($case.Invalid, 'preparation-invalid')
                        Invoke-IsolatedRuntimeComponentPromotion `
                            -Transaction $case.Transaction
                        if ($phase -ceq 'ready') {
                            [IO.File]::WriteAllText($case.Ready, 'new-ready')
                        }
                        else {
                            Remove-Item -LiteralPath $case.Invalid -Force
                        }
                        Restore-IsolatedRuntimePromotionTransaction `
                            -Transaction $case.Transaction
                        if ((Get-OutputTreeBytes -Root $case.Output) -cne $case.Before) {
                            throw "Post-promotion failure was not restored: $phase"
                        }
                    }

                    $failed = New-TransactionCase -Name 'rollback-failed'
                    [IO.File]::WriteAllText($failed.Invalid, 'preparation-invalid')
                    $env:GEORAEPLAN_PREPARATION_FAULT_POINT = 'component:Server'
                    try {
                        Invoke-IsolatedRuntimeComponentPromotion `
                            -Transaction $failed.Transaction
                    }
                    catch {
                    }
                    finally {
                        $env:GEORAEPLAN_PREPARATION_FAULT_POINT = $null
                    }
                    $env:GEORAEPLAN_PREPARATION_ROLLBACK_FAULT_POINT =
                        'rollback:App'
                    $rollbackFailed = $false
                    try {
                        Restore-IsolatedRuntimePromotionTransaction `
                            -Transaction $failed.Transaction
                    }
                    catch {
                        $rollbackFailed = $true
                    }
                    finally {
                        $env:GEORAEPLAN_PREPARATION_ROLLBACK_FAULT_POINT = $null
                    }
                    if (-not $rollbackFailed) {
                        throw 'Rollback failure injection was not observed.'
                    }
                    $readyStillExists =
                        Test-Path -LiteralPath $failed.Ready
                    $invalidMissing = -not (
                        Test-Path -LiteralPath $failed.Invalid -PathType Leaf)
                    $rollbackEvidenceMissing = -not (
                        Test-Path -LiteralPath (
                            Join-Path $failed.Quarantine 'rollback-failure.txt'))
                    $stageMissing = -not (
                        Test-Path -LiteralPath $failed.Stage)
                    $backupMissing = -not (
                        Test-Path -LiteralPath $failed.Backup)
                    if (
                        $readyStillExists -or
                        $invalidMissing -or
                        $rollbackEvidenceMissing -or
                        $stageMissing -or
                        $backupMissing
                    ) {
                        throw 'Rollback failure did not retain fail-closed evidence.'
                    }
                    Write-Output 'promotion_transaction_faults_verified'
                }
                finally {
                    $env:GEORAEPLAN_PREPARATION_FAULT_POINT = $null
                    $env:GEORAEPLAN_PREPARATION_ROLLBACK_FAULT_POINT = $null
                }
                """,
                new UTF8Encoding(true));

            var result = await RunPowerShellAsync(
                ResolveWindowsPowerShellPath(),
                harnessPath,
                TimeSpan.FromSeconds(40),
                "-SourceScript",
                ResolvePreparationScript(),
                "-TestRoot",
                testRoot);
            Assert.True(
                result.ExitCode == 0,
                "Promotion harness failed." +
                Environment.NewLine +
                "STDOUT:" +
                Environment.NewLine +
                result.Stdout +
                Environment.NewLine +
                "STDERR:" +
                Environment.NewLine +
                result.Stderr);
            Assert.Contains(
                "promotion_transaction_faults_verified",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.True(
                string.IsNullOrWhiteSpace(result.Stderr),
                result.Stderr);
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    private static RepeatablePreparationFixture
        CreateRepeatablePreparationFixture(
            string sourceScript,
            string testRoot)
    {
        var projectRoot = Path.Combine(testRoot, "project-root");
        var scriptRoot = Path.Combine(testRoot, "script-root");
        var sourceAppRoot = Path.Combine(testRoot, "source-app-root");
        var toolingRoot = Path.Combine(testRoot, "tooling");
        var snapshotTempRoot = Path.Combine(testRoot, "snapshot-temp");
        var copiedScript = Path.Combine(scriptRoot, "prepare-copy.ps1");
        var invocationScript = Path.Combine(
            testRoot,
            "invoke-repeatable-preparation.ps1");
        var fakeDotnet = Path.Combine(toolingRoot, "fake-dotnet.cmd");
        var fakeDotnetImplementation = Path.Combine(
            toolingRoot,
            "fake-dotnet.ps1");
        var dotnetInvocationLog = Path.Combine(
            testRoot,
            "dotnet-invocations.log");
        var utf8NoBom = new UTF8Encoding(false);

        Directory.CreateDirectory(Path.Combine(
            projectRoot,
            "Desktop",
            "Fixture.Desktop.App"));
        Directory.CreateDirectory(Path.Combine(
            projectRoot,
            "Server",
            "Fixture.Server.Api"));
        Directory.CreateDirectory(Path.Combine(
            projectRoot,
            "Mobile",
            "GeoraePlan.Mobile.App"));
        Directory.CreateDirectory(Path.Combine(
            projectRoot,
            "tools",
            "SyncDiag"));
        Directory.CreateDirectory(Path.Combine(
            projectRoot,
            "tools",
            "mobile"));
        Directory.CreateDirectory(Path.Combine(
            projectRoot,
            "fixture-deployment"));
        Directory.CreateDirectory(scriptRoot);
        Directory.CreateDirectory(sourceAppRoot);
        Directory.CreateDirectory(toolingRoot);
        Directory.CreateDirectory(snapshotTempRoot);

        File.WriteAllText(
            Path.Combine(projectRoot, "Fixture.sln"),
            string.Empty,
            utf8NoBom);
        File.WriteAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "Fixture.Desktop.App",
                "Fixture.Desktop.App.csproj"),
            "<Project />",
            utf8NoBom);
        File.WriteAllText(
            Path.Combine(
                projectRoot,
                "Server",
                "Fixture.Server.Api",
                "Fixture.Server.Api.csproj"),
            "<Project />",
            utf8NoBom);
        File.WriteAllText(
            Path.Combine(
                projectRoot,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "GeoraePlan.Mobile.App.csproj"),
            """
            <Project>
              <PropertyGroup>
                <ApplicationId>kr.georaeplan.mobile</ApplicationId>
                <ApplicationDisplayVersion>0.2.81</ApplicationDisplayVersion>
                <ApplicationVersion>192</ApplicationVersion>
              </PropertyGroup>
            </Project>
            """,
            utf8NoBom);
        File.WriteAllText(
            Path.Combine(
                projectRoot,
                "tools",
                "SyncDiag",
                "SyncDiag.csproj"),
            "<Project />",
            utf8NoBom);
        File.Copy(
            Path.Combine(
                FindRepositoryRoot(),
                "tools",
                "mobile",
                "AndroidApkMetadata.ps1"),
            Path.Combine(
                projectRoot,
                "tools",
                "mobile",
                "AndroidApkMetadata.ps1"));
        File.WriteAllText(
            Path.Combine(
                projectRoot,
                "fixture-deployment",
                "Set-ApiBaseUrl.ps1"),
            """
            param(
                [string]$BaseUrl,
                [string[]]$AppSettingsPaths
            )
            foreach ($path in $AppSettingsPaths) {
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                    [IO.File]::WriteAllText($path, '{}')
                }
            }
            """,
            utf8NoBom);
        File.WriteAllText(
            Path.Combine(
                scriptRoot,
                "\uAC80\uC99D \uCCB4\uD06C\uB9AC\uC2A4\uD2B8 \uD15C\uD50C\uB9BF.md"),
            "# fixture",
            utf8NoBom);

        var fixtureBuildCacheRoot = Path.Combine(testRoot, "build-cache");
        var preparationSource = ReplaceBuildCacheRoot(
            File.ReadAllText(sourceScript),
            fixtureBuildCacheRoot);
        ProvisionBuildCacheRoot(fixtureBuildCacheRoot);
        var seedFunctionStart = preparationSource.IndexOf(
            "function Initialize-IsolatedServerData {",
            StringComparison.Ordinal);
        var mainStart = preparationSource.IndexOf(
            "$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path",
            seedFunctionStart,
            StringComparison.Ordinal);
        var transactionHelpersStart = preparationSource.IndexOf(
            "function Invoke-TestEnvironmentPreparationFaultPoint {",
            seedFunctionStart,
            StringComparison.Ordinal);
        Assert.True(
            seedFunctionStart >= 0 &&
            transactionHelpersStart > seedFunctionStart &&
            mainStart > transactionHelpersStart);
        var seedStub =
            """
            function Initialize-IsolatedServerData {
                param(
                    [Parameter(Mandatory = $true)][string]$DotnetExe,
                    [Parameter(Mandatory = $true)][string]$SyncDiagProject,
                    [Parameter(Mandatory = $true)][string]$TestAppRoot,
                    [Parameter(Mandatory = $true)][string]$ServerDll,
                    [Parameter(Mandatory = $true)][string]$ServerWorkingDirectory,
                    [Parameter(Mandatory = $true)][string]$SeedLogRoot,
                    [Parameter(Mandatory = $true)][string]$ServerDataRoot,
                    [Parameter(Mandatory = $true)][string]$SourceApiBaseUrl,
                    [AllowNull()][object]$SourceUsersSnapshot,
                    [switch]$ResetAllUserPasswords,
                    [switch]$ResetUnresolvedPasswords
                )

                $stagePreparationLock =
                    Join-Path `
                        (Split-Path -Parent $TestAppRoot) `
                        '.georaeplan-prepare.lock'
                if (-not (Test-Path -LiteralPath $stagePreparationLock -PathType Leaf)) {
                    throw 'The staged preparation lifetime lease is missing.'
                }
                $exclusiveProbe = $null
                $exclusiveOpenSucceeded = $false
                try {
                    $exclusiveProbe = [IO.File]::Open(
                        $stagePreparationLock,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::ReadWrite,
                        [IO.FileShare]::None)
                    $exclusiveOpenSucceeded = $true
                }
                catch [IO.IOException] {
                    # The preparation parent must still own the write lease.
                }
                finally {
                    if ($null -ne $exclusiveProbe) {
                        $exclusiveProbe.Dispose()
                    }
                }
                if ($exclusiveOpenSucceeded) {
                    throw 'The staged preparation lifetime lease was not held.'
                }
                $childLeaseProbe = [IO.File]::Open(
                    $stagePreparationLock,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    [IO.FileShare]::ReadWrite)
                $childLeaseProbe.Dispose()
                $parentPresenceProbe = $null
                $parentPresenceOpenSucceeded = $false
                try {
                    $parentPresenceProbe = [IO.File]::Open(
                        $stagePreparationLock,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Write,
                        [IO.FileShare]::ReadWrite)
                    $parentPresenceOpenSucceeded = $true
                }
                catch [IO.IOException] {
                    # A held parent write lease must deny another writer.
                }
                finally {
                    if ($null -ne $parentPresenceProbe) {
                        $parentPresenceProbe.Dispose()
                    }
                }
                if ($parentPresenceOpenSucceeded) {
                    throw 'The staged parent write lease was not retained.'
                }
                Write-Output 'stage_preparation_lease_verified'

                Write-Utf8File `
                    -Path (
                        Join-Path `
                            $TestAppRoot `
                            '.georaeplan-isolated-seed-root') `
                    -Content $TestAppRoot
                Write-Utf8File `
                    -Path (
                        Join-Path `
                            $ServerWorkingDirectory `
                            '.georaeplan-isolated-server-root') `
                    -Content $ServerWorkingDirectory
                $retainedDatabase =
                    Join-Path $TestAppRoot 'data\거래플랜.db'
                $retainedAttachment =
                    Join-Path `
                        $TestAppRoot `
                        'attachments\retained-sentinel.txt'
                if (-not (Test-Path -LiteralPath $retainedDatabase -PathType Leaf)) {
                    throw 'The retained fixture database was not preserved.'
                }
                if (-not (Test-Path -LiteralPath $retainedAttachment -PathType Leaf)) {
                    throw 'The retained fixture attachment was not preserved.'
                }
                New-Item -ItemType Directory -Force -Path $SeedLogRoot |
                    Out-Null
                New-Item -ItemType Directory -Force -Path $ServerDataRoot |
                    Out-Null
                [IO.File]::WriteAllText(
                    (Join-Path $ServerWorkingDirectory '거래플랜-local.db'),
                    'fixture server database')
                [IO.File]::WriteAllText(
                    (Join-Path $SeedLogRoot 'resolved-users.json'),
                    '[{"Username":"fixture-admin","PasswordResolved":true,' +
                    '"PasswordWasReset":false}]')
            }

            """;
        File.WriteAllText(
            copiedScript,
            preparationSource[..seedFunctionStart] +
            seedStub +
            preparationSource[transactionHelpersStart..mainStart] +
            preparationSource[mainStart..],
            new UTF8Encoding(true));

        File.WriteAllText(
            fakeDotnetImplementation,
            """
            $allArguments = @($args | ForEach-Object { [string]$_ })
            if ($allArguments.Count -gt 0 -and
                $allArguments[0] -eq '--version') {
                Write-Output '8.0.100'
                exit 0
            }
            [IO.File]::AppendAllText(
                $env:FAKE_DOTNET_LOG,
                (($allArguments -join ' ') + [Environment]::NewLine))
            if (
                $allArguments.Count -gt 0 -and
                $allArguments[0] -eq 'build' -and
                $env:FAKE_DOTNET_FAIL_BUILD -eq '1'
            ) {
                exit 73
            }
            if (
                $allArguments.Count -gt 0 -and
                $allArguments[$allArguments.Count - 1] -eq
                    'stored-credential-envelopes'
            ) {
                $expectedFinalAppRoot =
                    [IO.Path]::GetFullPath(
                        $env:EXPECTED_ISOLATED_APP_ROOT).TrimEnd('\')
                $expectedOutputRoot = Split-Path -Parent $expectedFinalAppRoot
                $expectedOutputParent = Split-Path -Parent $expectedOutputRoot
                $expectedOutputLeaf = Split-Path -Leaf $expectedOutputRoot
                $actualAppRoot =
                    [IO.Path]::GetFullPath(
                        $env:GEORAEPLAN_APP_ROOT).TrimEnd('\')
                $actualStageRoot = Split-Path -Parent $actualAppRoot
                $actualStageLeaf = Split-Path -Leaf $actualStageRoot
                $expectedStagePattern =
                    '^\.georaeplan-stage-' +
                    [regex]::Escape($expectedOutputLeaf) +
                    '-[0-9a-fA-F]{32}$'
                if (
                    -not [string]::Equals(
                        (Split-Path -Leaf $actualAppRoot),
                        'AppData',
                        [StringComparison]::OrdinalIgnoreCase) -or
                    -not [string]::Equals(
                        (Split-Path -Parent $actualStageRoot),
                        $expectedOutputParent,
                        [StringComparison]::OrdinalIgnoreCase) -or
                    $actualStageLeaf -cnotmatch $expectedStagePattern
                ) {
                    throw 'Stored credentials were not read from private staged AppData.'
                }
                $typedMarker =
                    Join-Path $actualAppRoot '.georaeplan-isolated-seed-root'
                if (
                    -not (Test-Path -LiteralPath $typedMarker -PathType Leaf) -or
                    -not [string]::Equals(
                        [IO.File]::ReadAllText($typedMarker),
                        $actualAppRoot,
                        [StringComparison]::OrdinalIgnoreCase)
                ) {
                    throw 'Stored credential staged AppData marker is not typed to itself.'
                }
                if ([string]::Equals(
                        $actualAppRoot,
                        $env:FORBIDDEN_SOURCE_APP_ROOT,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Stored credentials were read from SourceAppRoot.'
                }
                [IO.File]::AppendAllText(
                    $env:FAKE_DOTNET_LOG,
                    ('stored-credential-envelopes-root=isolated' +
                        [Environment]::NewLine))
                if (-not [string]::IsNullOrWhiteSpace(
                        $env:FAKE_STORED_CREDENTIALS_JSON)) {
                    Write-Output $env:FAKE_STORED_CREDENTIALS_JSON
                }
                else {
                    Add-Type -AssemblyName System.Security
                    $plain = [Text.Encoding]::UTF8.GetBytes(
                        'fixture-stored-secret')
                    $protected = $null
                    try {
                        $protected =
                            [System.Security.Cryptography.ProtectedData]::Protect(
                                $plain,
                                $null,
                                [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
                        $ciphertext =
                            [Convert]::ToBase64String($protected)
                        Write-Output (
                            '{"schemaVersion":1,' +
                            '"protection":"DPAPI-CurrentUser",' +
                            '"credentials":[{"OfficeCode":"USENET",' +
                            '"TenantCode":"USENET_GROUP",' +
                            '"Username":"fixture-admin",' +
                            '"PasswordProtected":"' + $ciphertext + '",' +
                            '"SavedAtUtc":"2026-07-29T00:00:00.0000000Z"}]}')
                    }
                    finally {
                        [Array]::Clear($plain, 0, $plain.Length)
                        if ($null -ne $protected) {
                            [Array]::Clear(
                                $protected,
                                0,
                                $protected.Length)
                        }
                    }
                }
                exit 0
            }
            $snapshotCommandIndex = [Array]::IndexOf(
                [string[]]$allArguments,
                'snapshot-sqlite')
            if (
                $snapshotCommandIndex -ge 0 -and
                $snapshotCommandIndex + 2 -lt $allArguments.Count
            ) {
                $sourceDatabase =
                    $allArguments[$snapshotCommandIndex + 1]
                $targetDatabase =
                    $allArguments[$snapshotCommandIndex + 2]
                New-Item `
                    -ItemType Directory `
                    -Force `
                    -Path (Split-Path -Parent $targetDatabase) |
                    Out-Null
                Copy-Item `
                    -LiteralPath $sourceDatabase `
                    -Destination $targetDatabase `
                    -Force
                $targetSha256 = (
                    Get-FileHash `
                        -LiteralPath $targetDatabase `
                        -Algorithm SHA256).Hash
                Write-Output 'snapshot_succeeded=True'
                Write-Output 'quick_check=ok'
                Write-Output 'sidecar_count=0'
                Write-Output "target_sha256=$targetSha256"
                exit 0
            }
            if ($allArguments.Count -eq 0 -or
                $allArguments[0] -ne 'publish') {
                exit 0
            }
            $outputIndex = [Array]::IndexOf(
                [string[]]$allArguments,
                '-o')
            if ($outputIndex -lt 0 -or
                $outputIndex + 1 -ge $allArguments.Count) {
                throw 'Fake publish did not receive an output directory.'
            }
            $outputRoot = $allArguments[$outputIndex + 1]
            New-Item -ItemType Directory -Force -Path $outputRoot |
                Out-Null
            if ($allArguments[1] -like '*SyncDiag*') {
                [IO.File]::WriteAllText(
                    (Join-Path $outputRoot 'SyncDiag.dll'),
                    'fixture syncdiag assembly')
            }
            elseif ($allArguments[1] -like '*Desktop*') {
                [IO.File]::WriteAllText(
                    (Join-Path $outputRoot 'Fixture.Desktop.App.exe'),
                    'fixture desktop executable')
                [IO.File]::WriteAllText(
                    (Join-Path $outputRoot 'appsettings.json'),
                    '{}')
            }
            else {
                [IO.File]::WriteAllText(
                    (Join-Path $outputRoot '거래플랜.Server.Api.dll'),
                    'fixture server assembly')
            }
            exit 0
            """,
            new UTF8Encoding(true));
        File.WriteAllText(
            fakeDotnet,
            """
            @echo off
            powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0fake-dotnet.ps1" %*
            exit /b %ERRORLEVEL%
            """,
            Encoding.ASCII);
        File.WriteAllText(
            invocationScript,
            """
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][string]$PreparationScript,
                [Parameter(Mandatory = $true)][string]$ProjectRoot,
                [Parameter(Mandatory = $true)][string]$OutputRoot,
                [Parameter(Mandatory = $true)][string]$SourceAppRoot,
                [Parameter(Mandatory = $true)][string]$FakeDotnet,
                [Parameter(Mandatory = $true)][string]$DotnetInvocationLog,
                [Parameter(Mandatory = $true)][string]$SnapshotTempRoot,
                [string]$SourceUsersSnapshotPath = '',
                [string]$SourceUsersSnapshotSha256 = '',
                [string]$StoredCredentialsJson = '',
                [switch]$CopySourceData,
                [switch]$RunBuild,
                [switch]$FailBuild
            )

            $ErrorActionPreference = 'Stop'
            $env:DOTNET_EXE = $FakeDotnet
            $env:FAKE_DOTNET_LOG = $DotnetInvocationLog
            $env:EXPECTED_ISOLATED_APP_ROOT =
                Join-Path $OutputRoot 'AppData'
            $env:FORBIDDEN_SOURCE_APP_ROOT = $SourceAppRoot
            $env:TEMP = $SnapshotTempRoot
            $env:TMP = $SnapshotTempRoot
            $env:FAKE_STORED_CREDENTIALS_JSON =
                $StoredCredentialsJson
            $env:FAKE_DOTNET_FAIL_BUILD = if ($FailBuild) { '1' } else { '0' }
            $preparationParameters = @{
                ProjectRoot = $ProjectRoot
                OutputRoot = $OutputRoot
                SourceAppRoot = $SourceAppRoot
                SourceApiBaseUrl = 'http://127.0.0.1:1'
            }
            if (-not $RunBuild) {
                $preparationParameters.SkipBuild = $true
            }
            if (-not $CopySourceData) {
                $preparationParameters.SkipDataCopy = $true
            }
            if (-not [string]::IsNullOrWhiteSpace(
                    $SourceUsersSnapshotPath)) {
                $preparationParameters.SourceUsersSnapshotPath =
                    $SourceUsersSnapshotPath
                $preparationParameters.SourceUsersSnapshotSha256 =
                    $SourceUsersSnapshotSha256
                $env:FIXTURE_SOURCE_USERS_SNAPSHOT_ROOT =
                    Split-Path -Parent $SourceUsersSnapshotPath
            }
            & $PreparationScript @preparationParameters
            """,
            utf8NoBom);

        return new RepeatablePreparationFixture(
            projectRoot,
            sourceAppRoot,
            copiedScript,
            invocationScript,
            fakeDotnet,
            dotnetInvocationLog,
            snapshotTempRoot);
    }

    private static void AssertRunAllAutoLoginContract(string source)
    {
        const string passwordVariable = "$runScopedAdminPassword";
        const string autoLoginKey = "'GEORAEPLAN_TEST_AUTO_LOGIN'";
        const string usernameKey = "'GEORAEPLAN_TEST_AUTO_LOGIN_USERNAME'";
        const string passwordKey = "'GEORAEPLAN_TEST_AUTO_LOGIN_PASSWORD'";

        Assert.Equal(1, CountOccurrences(source, autoLoginKey));
        Assert.Equal(1, CountOccurrences(source, usernameKey));
        Assert.Equal(1, CountOccurrences(source, passwordKey));
        Assert.Equal(3, CountOccurrences(source, passwordVariable));
        Assert.Contains(
            "$runScopedAdminPassword = New-LocalTestPassword",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-AdminPassword $runScopedAdminPassword",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'GEORAEPLAN_TEST_AUTO_LOGIN_PASSWORD' = $runScopedAdminPassword",
            source,
            StringComparison.Ordinal);

        var launchLogIndex = source.IndexOf(
            "Write-Log 'Launching test app.'",
            StringComparison.Ordinal);
        var previousEnvironmentIndex = source.IndexOf(
            "$previousAutoLoginEnvironment = @{}",
            launchLogIndex,
            StringComparison.Ordinal);
        var captureIndex = source.IndexOf(
            "[Environment]::GetEnvironmentVariable($key, 'Process')",
            previousEnvironmentIndex,
            StringComparison.Ordinal);
        var tryIndex = source.IndexOf(
            "    try {",
            captureIndex,
            StringComparison.Ordinal);
        var setIndex = source.IndexOf(
            "[Environment]::SetEnvironmentVariable(",
            tryIndex,
            StringComparison.Ordinal);
        var appStartIndex = source.IndexOf(
            "$appProcess = Start-Process `",
            setIndex,
            StringComparison.Ordinal);
        var finallyIndex = source.IndexOf(
            "    finally {",
            appStartIndex,
            StringComparison.Ordinal);
        var restoreIndex = source.IndexOf(
            "[Environment]::SetEnvironmentVariable(",
            finallyIndex,
            StringComparison.Ordinal);
        var jobAssignmentIndex = source.IndexOf(
            "$childProcessJob.AssignProcess($appProcess)",
            finallyIndex,
            StringComparison.Ordinal);

        Assert.True(
            launchLogIndex >= 0 &&
            previousEnvironmentIndex > launchLogIndex &&
            captureIndex > previousEnvironmentIndex &&
            tryIndex > captureIndex &&
            setIndex > tryIndex &&
            appStartIndex > setIndex &&
            finallyIndex > appStartIndex &&
            restoreIndex > finallyIndex &&
            jobAssignmentIndex > restoreIndex,
            "The app-only auto-login environment is not captured, set, launched, " +
            "and restored in the required order.");

        var appLaunchBlock = source[launchLogIndex..jobAssignmentIndex];
        Assert.Contains(autoLoginKey, appLaunchBlock, StringComparison.Ordinal);
        Assert.Contains(usernameKey, appLaunchBlock, StringComparison.Ordinal);
        Assert.Contains(passwordKey, appLaunchBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-WindowStyle Hidden",
            appLaunchBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CreateNoWindow",
            appLaunchBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Write-Log $runScopedAdminPassword",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-ArgumentList $runScopedAdminPassword",
            source,
            StringComparison.Ordinal);

        Assert.Equal(
            9,
            CountOccurrences(source, "Stop-RunAllWithEarlyFailure"));
        Assert.Contains("EARLY FAILURE:", source, StringComparison.Ordinal);
        Assert.Contains(
            "Error log: $writtenErrorLogPath",
            source,
            StringComparison.Ordinal);
    }

    private static Dictionary<string, byte[]> CapturePreparedRuntimeExecutionBytes(
        string outputRoot)
    {
        return Directory.EnumerateFileSystemEntries(
                outputRoot,
                "*",
                SearchOption.AllDirectories)
            .ToDictionary(
                path =>
                    (Directory.Exists(path) ? "D:" : "F:") +
                    Path.GetRelativePath(outputRoot, path),
                path => Directory.Exists(path)
                    ? Array.Empty<byte>()
                    : File.ReadAllBytes(path),
                StringComparer.OrdinalIgnoreCase);
    }

    private static void ConfigureRepeatableFixtureBuildCacheRoot(
        RepeatablePreparationFixture fixture,
        string buildCacheRoot,
        bool provision = true)
    {
        var source = File.ReadAllText(fixture.CopiedScript);
        File.WriteAllText(
            fixture.CopiedScript,
            ReplaceBuildCacheRoot(source, buildCacheRoot),
            new UTF8Encoding(true));
        if (provision)
        {
            ProvisionBuildCacheRoot(buildCacheRoot);
        }
    }

    private static string ReplaceBuildCacheRoot(
        string source,
        string buildCacheRoot)
    {
        const string assignmentPrefix = "$buildCacheRoot = '";
        var assignmentStart = source.IndexOf(
            assignmentPrefix,
            StringComparison.Ordinal);
        Assert.True(assignmentStart >= 0);
        var lineEnd = source.IndexOfAny(
            new[] { '\r', '\n' },
            assignmentStart);
        if (lineEnd < 0)
        {
            lineEnd = source.Length;
        }

        var escapedRoot = buildCacheRoot.Replace("'", "''");
        return source[..assignmentStart] +
            assignmentPrefix +
            escapedRoot +
            "'" +
            source[lineEnd..];
    }

    private static void ProvisionBuildCacheRoot(string buildCacheRoot)
    {
        foreach (var relativePath in new[]
                 {
                     "temp",
                     Path.Combine("nuget", "packages"),
                     Path.Combine("nuget", "http-cache"),
                     Path.Combine("nuget", "plugins-cache"),
                     "dotnet-home",
                 })
        {
            var cachePath = Path.Combine(buildCacheRoot, relativePath);
            Directory.CreateDirectory(cachePath);
            File.WriteAllText(
                Path.Combine(cachePath, ".georaeplan-build-cache-lease"),
                string.Empty,
                new UTF8Encoding(false));
        }
    }

    private static void InjectRepeatableFixtureBuildCacheSwapProbe(
        RepeatablePreparationFixture fixture,
        string swapTarget,
        string resultPath)
    {
        const string insertionPoint =
            "    $dotnetExe = Resolve-DotnetCommand -ProjectRoot $ProjectRoot";
        var source = File.ReadAllText(fixture.CopiedScript);
        Assert.Equal(1, CountOccurrences(source, insertionPoint));
        var escapedTarget = swapTarget.Replace("'", "''");
        var escapedResult = resultPath.Replace("'", "''");
        var probe = $$"""
            $cacheSwapTarget = '{{escapedTarget}}'
            $cacheSwapResultPath = '{{escapedResult}}'
            $cacheLeafEntries = @(
                $buildEnvironmentPreflightLease.Leases |
                    ForEach-Object { $_.Entries } |
                    Where-Object {
                        [string]::Equals(
                            [string]$_.Path,
                            [IO.Path]::GetFullPath($env:TEMP),
                            [StringComparison]::OrdinalIgnoreCase)
                    })
            $openCacheLeafHandles = @(
                $cacheLeafEntries |
                    Where-Object { -not $_.Handle.IsClosed }).Count
            $cacheLeafRemoved = $false
            $cacheSwapResult = 'blocked'
            try {
                Remove-Item -LiteralPath $env:TEMP -Recurse -Force -ErrorAction Stop
                $cacheLeafRemoved = $true
                $quotedCachePath = '"' + $env:TEMP + '"'
                $quotedSwapTarget = '"' + $cacheSwapTarget + '"'
                & $env:ComSpec /d /c (
                    'mklink /J ' + $quotedCachePath + ' ' + $quotedSwapTarget) |
                    Out-Null
                if ($LASTEXITCODE -ne 0) {
                    throw 'cache swap junction creation failed'
                }
                [IO.File]::WriteAllText(
                    (Join-Path $env:TEMP 'cache-swap-escaped.txt'),
                    'escaped')
                $cacheSwapResult = 'swapped'
            }
            catch {
                if ($cacheLeafRemoved) {
                    $cacheSwapResult = 'junction-unavailable'
                }
            }
            [IO.File]::WriteAllText(
                $cacheSwapResultPath,
                ($cacheSwapResult +
                    '|leaf=' + $cacheLeafEntries.Count +
                    '|open=' + $openCacheLeafHandles))

            """;
        File.WriteAllText(
            fixture.CopiedScript,
            source.Replace(
                insertionPoint,
                probe + insertionPoint,
                StringComparison.Ordinal),
            new UTF8Encoding(true));
    }

    private static void InjectRepeatableFixtureBuildCachePreMutationLeaseSwapProbe(
        RepeatablePreparationFixture fixture,
        string swapTarget,
        string resultPath)
    {
        var source = File.ReadAllText(fixture.CopiedScript);
        var functionStart = source.IndexOf(
            "function Enter-IsolatedBuildCacheLeafMutationLease",
            StringComparison.Ordinal);
        Assert.True(functionStart >= 0);
        var insertionIndex = source.IndexOf(
            "        $stream = [IO.File]::Open(",
            functionStart,
            StringComparison.Ordinal);
        Assert.True(insertionIndex > functionStart);
        var escapedTarget = swapTarget.Replace("'", "''");
        var escapedResult = resultPath.Replace("'", "''");
        var probe = $$"""
                $cacheSwapTarget = '{{escapedTarget}}'
                $cacheSwapResultPath = '{{escapedResult}}'
                $cacheLeafRemoved = $false
                $cacheSwapResult = 'blocked'
                try {
                    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
                    $cacheLeafRemoved = $true
                    $quotedCachePath = '"' + $Path + '"'
                    $quotedSwapTarget = '"' + $cacheSwapTarget + '"'
                    & $env:ComSpec /d /c (
                        'mklink /J ' + $quotedCachePath + ' ' + $quotedSwapTarget) |
                        Out-Null
                    if ($LASTEXITCODE -ne 0) {
                        throw 'cache swap junction creation failed'
                    }
                    $cacheSwapResult = 'swapped'
                }
                catch {
                    if ($cacheLeafRemoved) {
                        $cacheSwapResult = 'junction-unavailable'
                    }
                }
                [IO.File]::WriteAllText(
                    $cacheSwapResultPath,
                    ($cacheSwapResult + '|before-mutation-lease'))

            """;
        File.WriteAllText(
            fixture.CopiedScript,
            source.Insert(insertionIndex, probe),
            new UTF8Encoding(true));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ConfigureSourceUsersSnapshotFixture(
        RepeatablePreparationFixture fixture,
        string snapshotRoot,
        string snapshotPath)
    {
        Directory.CreateDirectory(snapshotRoot);
        const string canonicalUsers =
            "[{\"username\":\"fixture-admin\",\"role\":\"Admin\"," +
            "\"tenantCode\":\"USENET_GROUP\",\"officeCode\":\"USENET\"," +
            "\"scopeType\":\"Admin\",\"isActive\":true," +
            "\"permissions\":[]}]";
        var snapshotPayload = new
        {
            schemaVersion = 1,
            sourceKind = "georaeplan-user-permission-snapshot-v1",
            generatedAtUtc = DateTime.UtcNow.ToString("O"),
            isComplete = true,
            userCount = 1,
            permissionCount = 0,
            scopeCounts = new[]
            {
                new
                {
                    tenantCode = "USENET_GROUP",
                    officeCode = "USENET",
                    role = "Admin",
                    scopeType = "Admin",
                    isActive = true,
                    userCount = 1,
                    permissionCount = 0
                }
            },
            canonicalSha256 = ComputeTextSha256(canonicalUsers),
            users = new[]
            {
                new
                {
                    username = "fixture-admin",
                    role = "Admin",
                    tenantCode = "USENET_GROUP",
                    officeCode = "USENET",
                    scopeType = "Admin",
                    isActive = true,
                    permissions = Array.Empty<string>()
                }
            }
        };
        File.WriteAllText(
            snapshotPath,
            JsonSerializer.Serialize(snapshotPayload),
            new UTF8Encoding(false));

        var copiedSource = File.ReadAllText(fixture.CopiedScript);
        const string allowedRootAssignment =
            """
            $sourceUsersSnapshotAllowedRoot = Join-Path `
                ([IO.Path]::GetPathRoot($ProjectRoot)) `
                'DevCaches\georaeplan-v1-user-snapshots'
            """;
        Assert.Contains(
            allowedRootAssignment,
            copiedSource,
            StringComparison.Ordinal);
        copiedSource = copiedSource.Replace(
            allowedRootAssignment,
            "$sourceUsersSnapshotAllowedRoot = " +
            "$env:FIXTURE_SOURCE_USERS_SNAPSHOT_ROOT",
            StringComparison.Ordinal);
        copiedSource = copiedSource.Replace(
            "        -RequireProtectedAcl",
            "        -RequireProtectedAcl:$false",
            StringComparison.Ordinal);
        File.WriteAllText(
            fixture.CopiedScript,
            copiedSource,
            new UTF8Encoding(true));

        return ComputeFileSha256(snapshotPath);
    }

    private static string ComputeFileSha256(string path)
    {
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(path)));
    }

    private static string ComputeTextSha256(string value)
    {
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                new UTF8Encoding(false).GetBytes(value)));
    }

    private static string ResolvePreparationScript()
    {
        var sourceScript = Path.Combine(
            FindRepositoryRoot(),
            "테스트 시행",
            "테스트-환경-준비.ps1");
        Assert.True(File.Exists(sourceScript), $"Preparation script not found: {sourceScript}");
        return sourceScript;
    }

    private static PreparationScriptFixture CreatePreparationScriptFixture(
        string sourceScript,
        string testRoot)
    {
        var projectRoot = Path.Combine(testRoot, "project-root");
        var scriptRoot = Path.Combine(testRoot, "script-root");
        var sourceAppRoot = Path.Combine(
            testRoot,
            "source-container",
            "source-app-root");
        var toolingRoot = Path.Combine(testRoot, "tooling");
        var copiedScript = Path.Combine(scriptRoot, "prepare-copy.ps1");
        var invocationScript = Path.Combine(testRoot, "invoke-preparation.ps1");
        var fakeDotnet = Path.Combine(toolingRoot, "fake-dotnet.cmd");
        var dotnetInvocationLog = Path.Combine(testRoot, "dotnet-invocations.log");
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        Directory.CreateDirectory(Path.Combine(projectRoot, "Desktop", "Fixture.Desktop.App"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "Server", "Fixture.Server.Api"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "tools", "SyncDiag"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "fixture-deployment"));
        Directory.CreateDirectory(scriptRoot);
        Directory.CreateDirectory(sourceAppRoot);
        Directory.CreateDirectory(toolingRoot);

        File.WriteAllText(Path.Combine(projectRoot, "Fixture.sln"), string.Empty, utf8NoBom);
        File.WriteAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "Fixture.Desktop.App",
                "Fixture.Desktop.App.csproj"),
            "<Project />",
            utf8NoBom);
        File.WriteAllText(
            Path.Combine(
                projectRoot,
                "Server",
                "Fixture.Server.Api",
                "Fixture.Server.Api.csproj"),
            "<Project />",
            utf8NoBom);
        File.WriteAllText(
            Path.Combine(projectRoot, "tools", "SyncDiag", "SyncDiag.csproj"),
            "<Project />",
            utf8NoBom);
        File.WriteAllText(
            Path.Combine(
                projectRoot,
                "fixture-deployment",
                "Set-ApiBaseUrl.ps1"),
            "param([string]$BaseUrl, [string[]]$AppSettingsPaths)",
            utf8NoBom);
        File.WriteAllText(
            Path.Combine(scriptRoot, "검증 체크리스트 템플릿.md"),
            "# fixture",
            utf8NoBom);
        File.Copy(sourceScript, copiedScript);

        File.WriteAllText(
            fakeDotnet,
            """
            @echo off
            if /I "%~1"=="--version" (
              echo 8.0.100
              exit /b 0
            )
            >>"%FAKE_DOTNET_LOG%" echo %*
            if /I "%~1"=="publish" exit /b 87
            exit /b 0
            """,
            Encoding.ASCII);
        File.WriteAllText(
            invocationScript,
            """
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][string]$PreparationScript,
                [Parameter(Mandatory = $true)][string]$ProjectRoot,
                [Parameter(Mandatory = $true)][string]$OutputRoot,
                [Parameter(Mandatory = $true)][string]$SourceAppRoot,
                [Parameter(Mandatory = $true)][string]$FakeDotnet,
                [Parameter(Mandatory = $true)][string]$DotnetInvocationLog
            )

            $ErrorActionPreference = 'Stop'
            $env:DOTNET_EXE = $FakeDotnet
            $env:FAKE_DOTNET_LOG = $DotnetInvocationLog
            try {
                & $PreparationScript `
                    -ProjectRoot $ProjectRoot `
                    -OutputRoot $OutputRoot `
                    -SourceAppRoot $SourceAppRoot `
                    -SkipBuild `
                    -SkipDataCopy `
                    -SkipServerSeed
                throw "Preparation script accepted unsafe OutputRoot: $OutputRoot"
            }
            catch {
                [Console]::Error.WriteLine($_.Exception.ToString())
                exit 1
            }
            """,
            utf8NoBom);

        return new PreparationScriptFixture(
            projectRoot,
            scriptRoot,
            sourceAppRoot,
            copiedScript,
            invocationScript,
            fakeDotnet,
            dotnetInvocationLog);
    }

    private static async Task<PowerShellResult> RunPreparationScriptAsync(
        PreparationScriptFixture fixture,
        string outputRoot,
        TimeSpan timeout)
    {
        return await RunPowerShellAsync(
            ResolveWindowsPowerShellPath(),
            fixture.InvocationScript,
            timeout,
            "-PreparationScript",
            fixture.CopiedScript,
            "-ProjectRoot",
            fixture.ProjectRoot,
            "-OutputRoot",
            outputRoot,
            "-SourceAppRoot",
            fixture.SourceAppRoot,
            "-FakeDotnet",
            fixture.FakeDotnet,
            "-DotnetInvocationLog",
            fixture.DotnetInvocationLog);
    }

    private static void AssertPreparationFailedClosed(
        PowerShellResult result,
        string dotnetInvocationLog,
        string sourceMarker,
        string protectedRootName)
    {
        var diagnostic = result.Stdout + Environment.NewLine + result.Stderr;
        var dotnetInvocations = File.Exists(dotnetInvocationLog)
            ? File.ReadAllText(dotnetInvocationLog)
            : string.Empty;

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Unsafe OutputRoot:",
            diagnostic,
            StringComparison.Ordinal);
        Assert.True(
            File.Exists(sourceMarker),
            $"Preparation deleted source data before rejecting OutputRoot collision with {protectedRootName}.");
        Assert.DoesNotContain(
            "publish",
            dotnetInvocations,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        Assert.True(
            TryCreateDirectoryJunction(
                junctionPath,
                targetPath,
                out var failure),
            failure);
    }

    private static bool TryCreateDirectoryJunction(
        string junctionPath,
        string targetPath,
        out string failure)
    {
        var commandPath = Path.Combine(
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
            ?? throw new InvalidOperationException("The junction creation process did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var succeeded =
            process.ExitCode == 0 &&
            Directory.Exists(junctionPath) &&
            (File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0;
        failure =
            "Failed to create the D-drive junction fixture." +
            Environment.NewLine +
            "STDOUT:" + Environment.NewLine +
            stdout +
            Environment.NewLine +
            "STDERR:" + Environment.NewLine +
            stderr;
        return succeeded;
    }

    private static void CreateFileHardLink(string linkPath, string targetPath)
    {
        var commandPath = Path.Combine(
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
        startInfo.ArgumentList.Add("/H");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The hard-link creation process did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0 &&
            File.Exists(linkPath) &&
            File.Exists(targetPath),
            "Failed to create the D-drive hard-link fixture." +
            Environment.NewLine +
            "STDOUT:" + Environment.NewLine +
            stdout +
            Environment.NewLine +
            "STDERR:" + Environment.NewLine +
            stderr);
    }

    private static Process StartPowerShellProcess(
        string executablePath,
        string scriptPath,
        params string[] arguments)
    {
        var startInfo = CreatePowerShellStartInfo(
            executablePath,
            scriptPath,
            redirectOutput: false,
            arguments: arguments);
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException(
                   $"PowerShell process did not start: {scriptPath}");
    }

    private static async Task<PowerShellResult> RunPowerShellAsync(
        string executablePath,
        string scriptPath,
        TimeSpan timeout,
        params string[] arguments)
    {
        var startInfo = CreatePowerShellStartInfo(
            executablePath,
            scriptPath,
            redirectOutput: true,
            arguments: arguments);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"PowerShell process did not start: {scriptPath}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException ex)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                // The caller reports the timeout without waiting indefinitely on inherited handles.
            }

            throw new TimeoutException(
                "PowerShell process-safety probe timed out.",
                ex);
        }

        return new PowerShellResult(
            process.ExitCode,
            await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5)),
            await stderrTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(
        string executablePath,
        string scriptPath,
        bool redirectOutput,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)
                               ?? TestProcessIsolation.TempRoot
        };
        ConfigureWindowsPowerShellModulePath(startInfo, executablePath);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static void ConfigureWindowsPowerShellModulePath(
        ProcessStartInfo startInfo,
        string executablePath)
    {
        if (!string.Equals(
                Path.GetFileName(executablePath),
                "powershell.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var windowsPowerShellHome = Path.GetDirectoryName(
            Path.GetFullPath(executablePath));
        Assert.False(
            string.IsNullOrWhiteSpace(windowsPowerShellHome),
            $"Windows PowerShell home could not be resolved: {executablePath}");
        var modulePath = Path.Combine(
            windowsPowerShellHome!,
            "Modules");
        Assert.True(
            Directory.Exists(modulePath),
            $"Windows PowerShell module directory was not found: {modulePath}");
        startInfo.Environment["PSModulePath"] = modulePath;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(File.Exists(path), $"Timed out waiting for test marker: {path}");
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ResolveWindowsPowerShellPath()
    {
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(
            File.Exists(powerShellPath),
            $"Windows PowerShell not found: {powerShellPath}");
        return powerShellPath;
    }

    private static async Task DeleteDirectoryWithRetriesAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch when (attempt < 4)
            {
                await Task.Delay(100 * (attempt + 1));
            }
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

    private sealed record PowerShellResult(
        int ExitCode,
        string Stdout,
        string Stderr);

    private sealed record PreparationScriptFixture(
        string ProjectRoot,
        string ScriptRoot,
        string SourceAppRoot,
        string CopiedScript,
        string InvocationScript,
        string FakeDotnet,
        string DotnetInvocationLog);

    private sealed record RepeatablePreparationFixture(
        string ProjectRoot,
        string SourceAppRoot,
        string CopiedScript,
        string InvocationScript,
        string FakeDotnet,
        string DotnetInvocationLog,
        string SnapshotTempRoot);
}
