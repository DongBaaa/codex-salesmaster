using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class ClientCompatibilityDeploymentConfigurationTests
{
    [Fact]
    public async Task LinuxCompose_ExposesTwoDisabledPoliciesAndAuditOnlyDefault()
    {
        var repositoryRoot = FindRepositoryRoot();
        var compose =
            await File.ReadAllTextAsync(
                Path.Combine(
                    repositoryRoot,
                    "infra",
                    "linux",
                    "docker-compose.yml"));
        var environmentExample =
            await File.ReadAllTextAsync(
                Path.Combine(
                    repositoryRoot,
                    "infra",
                    "linux",
                    ".env.example"));

        Assert.Contains(
            "ClientCompatibility__Mode: ${CLIENT_COMPATIBILITY_MODE:-AuditOnly}",
            compose,
            StringComparison.Ordinal);
        AssertPolicyMapping(
            compose,
            index: 0,
            prefix: "DESKTOP",
            appId: "kr.georaeplan.desktop",
            platform: "windows");
        AssertPolicyMapping(
            compose,
            index: 1,
            prefix: "ANDROID",
            appId: "kr.georaeplan.mobile",
            platform: "android");

        Assert.Contains(
            "CLIENT_COMPATIBILITY_MODE=AuditOnly",
            environmentExample,
            StringComparison.Ordinal);
        Assert.Contains(
            "CLIENT_COMPATIBILITY_DESKTOP_ENABLED=false",
            environmentExample,
            StringComparison.Ordinal);
        Assert.Contains(
            "CLIENT_COMPATIBILITY_ANDROID_ENABLED=false",
            environmentExample,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CLIENT_COMPATIBILITY_MODE=StrictBlock",
            environmentExample,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessScripts_ValidateCompatibilitySummaryAndExpectedMode()
    {
        var repositoryRoot = FindRepositoryRoot();
        var operationalGate =
            await File.ReadAllTextAsync(
                Path.Combine(
                    repositoryRoot,
                    "tools",
                    "ops",
                    "Invoke-GeoraePlanOperationalGate.ps1"));
        var preLiveGate =
            await File.ReadAllTextAsync(
                Path.Combine(
                    repositoryRoot,
                    "tools",
                    "verification",
                    "Invoke-GeoraePlanPreLiveVerification.ps1"));
        var deployAfterTest =
            await File.ReadAllTextAsync(
                Path.Combine(
                    repositoryRoot,
                    "테스트 시행",
                    "Deploy-After-Test.ps1"));

        foreach (var source in new[]
                 {
                     operationalGate,
                     preLiveGate
                 })
        {
            Assert.Contains(
                "ExpectedClientCompatibilityMode",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "ExpectedClientCompatibilityEnabledPolicyCount",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "clientCompatibility",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "configuredPolicyCount",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "enabledPolicyCount",
                source,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "AllowMissingClientCompatibilitySummary",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(
                deployAfterTest,
                "'-AllowMissingClientCompatibilitySummary'"));
        Assert.Contains(
            "[switch]$AllowLegacyPreDeployCompatibilitySummary",
            deployAfterTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "-AllowMissingClientCompatibilitySummary ([bool]$AllowLegacyPreDeployCompatibilitySummary)",
            deployAfterTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$ExpectedClientCompatibilityMode = 'AuditOnly'",
            deployAfterTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$ExpectedClientCompatibilityEnabledPolicyCount = 0",
            deployAfterTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ValidateRange(0, 2)]",
            deployAfterTest,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessValidators_ExecuteAuditOnlyAndStrictBlockFixtures()
    {
        var paths = GetReadinessScriptPaths();
        foreach (var script in new[] { paths.Operational, paths.PreLive })
        {
            var audit =
                await RunCompatibilityFixtureAsync(
                    script,
                    AuditOnlySummary,
                    AuditOnlySummary,
                    expectedMode: "AuditOnly",
                    expectedCount: 0,
                    allowMissing: false);
            Assert.Equal(0, audit.ExitCode);

            var caseInsensitiveMode =
                AuditOnlySummary.Replace(
                    "\"AuditOnly\"",
                    "\"auditonly\"",
                    StringComparison.Ordinal);
            var normalizedMode =
                await RunCompatibilityFixtureAsync(
                    script,
                    caseInsensitiveMode,
                    caseInsensitiveMode,
                    expectedMode: "AUDITONLY",
                    expectedCount: 0,
                    allowMissing: false);
            Assert.Equal(0, normalizedMode.ExitCode);

            var strict =
                await RunCompatibilityFixtureAsync(
                    script,
                    StrictBlockSummary,
                    StrictBlockSummary,
                    expectedMode: "StrictBlock",
                    expectedCount: 2,
                    allowMissing: false);
            Assert.Equal(0, strict.ExitCode);

            var zeroMinimumVersion =
                StrictBlockSummary.Replace(
                    "\"minimumVersion\":\"1.1.689\"",
                    "\"minimumVersion\":\"0.0\"",
                    StringComparison.Ordinal);
            var zeroMinimum =
                await RunCompatibilityFixtureAsync(
                    script,
                    zeroMinimumVersion,
                    zeroMinimumVersion,
                    expectedMode: "StrictBlock",
                    expectedCount: 2,
                    allowMissing: false);
            Assert.Equal(0, zeroMinimum.ExitCode);
        }
    }

    [Fact]
    public async Task ReadinessValidators_RejectMalformedOrMismatchedSummaries()
    {
        var malformed = new[]
        {
            StrictBlockSummary.Replace(
                "\"requiresUserAction\":true",
                "\"requiresUserAction\":false",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"minimumBuild\":689",
                "\"minimumBuild\":0",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"minimumVersion\":\"1.1.689\",\"minimumBuild\":689,\"minimumProtocolVersion\":1",
                "\"minimumVersion\":\"\",\"minimumBuild\":null,\"minimumProtocolVersion\":null",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"latestVersion\":\"1.1.689\"",
                "\"latestVersion\":\"1.1.688\"",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"latestBuild\":689",
                "\"latestBuild\":688",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"appId\":\"kr.georaeplan.mobile\",\"platform\":\"android\"",
                "\"appId\":\"kr.georaeplan.desktop\",\"platform\":\"windows\"",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"appId\":\"kr.georaeplan.mobile\"",
                "\"appId\":\"kr.georaeplan.other\"",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"configuredPolicyCount\":2",
                "\"configuredPolicyCount\":101",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"configuredPolicyCount\":2",
                "\"configuredPolicyCount\":\"2\"",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"appId\":\"kr.georaeplan.desktop\"",
                "\"appId\":\" \"",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"policies\":[",
                "\"diagnostics\":{\"token\":\"must-not-appear\"},\"policies\":[",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"policyVersion\":7",
                "\"policyVersion\":7,\"unexpectedPolicyField\":true",
                StringComparison.Ordinal),
            StrictBlockSummary.Replace(
                "\"mode\":\"StrictBlock\"",
                "\"mode\":\"StrictBlock\",\"accessToken\":\"must-not-appear\"",
                StringComparison.Ordinal),
            "[" + StrictBlockSummary + "," +
                StrictBlockSummary + "]",
            AuditOnlySummary.Replace(
                "\"policies\":[]",
                "\"policies\":{}",
                StringComparison.Ordinal)
        };

        var paths = GetReadinessScriptPaths();
        foreach (var script in new[] { paths.Operational, paths.PreLive })
        {
            foreach (var fixture in malformed)
            {
                var result =
                    await RunCompatibilityFixtureAsync(
                        script,
                        fixture,
                        fixture,
                        expectedMode: "StrictBlock",
                        expectedCount: 2,
                        allowMissing: false);
                Assert.NotEqual(0, result.ExitCode);
            }

            var mismatchedReady =
                StrictBlockSummary.Replace(
                    "\"latestBuild\":193",
                    "\"latestBuild\":194",
                    StringComparison.Ordinal);
            var mismatch =
                await RunCompatibilityFixtureAsync(
                    script,
                    StrictBlockSummary,
                    mismatchedReady,
                    expectedMode: "StrictBlock",
                    expectedCount: 2,
                    allowMissing: false);
            Assert.NotEqual(0, mismatch.ExitCode);
        }
    }

    [Fact]
    public async Task OperationalLegacyAllowance_IsBoundedToBothMissingSummaries()
    {
        var operational = GetReadinessScriptPaths().Operational;
        var allowed =
            await RunCompatibilityFixtureAsync(
                operational,
                healthJson: null,
                readyJson: null,
                expectedMode: "AuditOnly",
                expectedCount: 0,
                allowMissing: true);
        Assert.True(
            allowed.ExitCode == 0,
            $"Expected compatibility fixture to pass, but it exited with {allowed.ExitCode}:{Environment.NewLine}{allowed.Output}");

        var notAllowed =
            await RunCompatibilityFixtureAsync(
                operational,
                healthJson: null,
                readyJson: null,
                expectedMode: "AuditOnly",
                expectedCount: 0,
                allowMissing: false);
        Assert.NotEqual(0, notAllowed.ExitCode);

        var onlyOneMissing =
            await RunCompatibilityFixtureAsync(
                operational,
                healthJson: null,
                readyJson: AuditOnlySummary,
                expectedMode: "AuditOnly",
                expectedCount: 0,
                allowMissing: true);
        Assert.NotEqual(0, onlyOneMissing.ExitCode);

        var preLive =
            await RunCompatibilityFixtureAsync(
                GetReadinessScriptPaths().PreLive,
                healthJson: null,
                readyJson: null,
                expectedMode: "AuditOnly",
                expectedCount: 0,
                allowMissing: true);
        Assert.NotEqual(0, preLive.ExitCode);
    }

    [Fact]
    public async Task DeployCompatibilityArguments_ExecuteExactNestedPlumbing()
    {
        var repositoryRoot = FindRepositoryRoot();
        var deployScript =
            Path.Combine(
                repositoryRoot,
                "테스트 시행",
                "Deploy-After-Test.ps1");
        var result =
            await RunDeployArgumentFixtureAsync(deployScript);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "outer=-ExpectedClientCompatibilityMode|StrictBlock|-ExpectedClientCompatibilityEnabledPolicyCount|2|-AllowMissingClientCompatibilitySummary",
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "publisher=-ExpectedClientCompatibilityMode|StrictBlock|-ExpectedClientCompatibilityEnabledPolicyCount|2|-AllowLegacyPreDeployCompatibilitySummary",
            result.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "publisher=-AllowMissingClientCompatibilitySummary",
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "rangeRejected=True",
            result.Output,
            StringComparison.Ordinal);
    }

    private const string AuditOnlySummary =
        """
        {"mode":"AuditOnly","configuredPolicyCount":2,"enabledPolicyCount":0,"policies":[]}
        """;

    private const string StrictBlockSummary =
        """
        {"mode":"StrictBlock","configuredPolicyCount":2,"enabledPolicyCount":2,"policies":[{"appId":"kr.georaeplan.desktop","platform":"windows","policyVersion":7,"requiresUserAction":true,"minimumVersion":"1.1.689","minimumBuild":689,"minimumProtocolVersion":1,"latestVersion":"1.1.689","latestBuild":689},{"appId":"kr.georaeplan.mobile","platform":"android","policyVersion":8,"requiresUserAction":true,"minimumVersion":"0.2.82","minimumBuild":193,"minimumProtocolVersion":1,"latestVersion":"0.2.82","latestBuild":193}]}
        """;

    private static (string Operational, string PreLive)
        GetReadinessScriptPaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        return (
            Path.Combine(
                repositoryRoot,
                "tools",
                "ops",
                "Invoke-GeoraePlanOperationalGate.ps1"),
            Path.Combine(
                repositoryRoot,
                "tools",
                "verification",
                "Invoke-GeoraePlanPreLiveVerification.ps1"));
    }

    private static async Task<PowerShellFixtureResult>
        RunCompatibilityFixtureAsync(
            string sourceScript,
            string? healthJson,
            string? readyJson,
            string expectedMode,
            int expectedCount,
            bool allowMissing)
    {
        const string harness =
            """
            param(
                [string]$SourceScript,
                [string]$Kind,
                [string]$HealthPath,
                [string]$ReadyPath,
                [string]$ExpectedMode,
                [int]$ExpectedCount,
                [int]$AllowMissing)
            $ErrorActionPreference = 'Stop'
            trap {
                Write-Error (
                    "fixture_failure={0}; position={1}; stack={2}" -f
                        $_.Exception.Message,
                        $_.InvocationInfo.PositionMessage,
                        $_.ScriptStackTrace)
                exit 1
            }
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $SourceScript,
                [ref]$tokens,
                [ref]$errors)
            if ($errors.Count -ne 0) {
                throw ($errors | ForEach-Object Message)
            }
            $requiredNames =
                if ($Kind -eq 'operational') {
                    @(
                        'Get-RequiredJsonPropertyValue',
                        'Assert-ExactCompatibilityObjectSchema',
                        'Assert-NoSensitiveCompatibilityField',
                        'ConvertTo-BoundedCompatibilityInteger',
                        'ConvertTo-CompatibilityVersion',
                        'ConvertTo-NormalizedClientCompatibilitySummary',
                        'Test-ClientCompatibilitySummaryPair')
                }
                else {
                    @(
                        'Assert-NoSensitiveCompatibilityField',
                        'ConvertTo-NormalizedClientCompatibilitySummary',
                        'Test-ClientCompatibilitySummaryPair')
                }
            $functions = @(
                $ast.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $requiredNames -contains $node.Name
                    },
                    $true))
            foreach ($name in $requiredNames) {
                $definition = @(
                    $functions |
                        Where-Object Name -eq $name)
                if ($definition.Count -ne 1) {
                    throw "Expected one function '$name', found $($definition.Count)."
                }
                . ([scriptblock]::Create($definition[0].Extent.Text))
            }
            function Read-Fixture([string]$Path) {
                if ($Path -eq '__GEORAEPLAN_NULL_FIXTURE__') {
                    return $null
                }
                return (
                    Get-Content -LiteralPath $Path -Raw -Encoding UTF8 |
                        ConvertFrom-Json)
            }
            $health = Read-Fixture $HealthPath
            $ready = Read-Fixture $ReadyPath
            if ($Kind -eq 'operational') {
                $result =
                    Test-ClientCompatibilitySummaryPair `
                        -HealthSummary $health `
                        -ReadySummary $ready `
                        -ExpectedMode $ExpectedMode `
                        -ExpectedEnabledPolicyCount $ExpectedCount `
                        -AllowMissing:([bool]$AllowMissing)
            }
            else {
                $result =
                    Test-ClientCompatibilitySummaryPair `
                        -HealthSummary $health `
                        -ReadySummary $ready `
                        -ExpectedMode $ExpectedMode `
                        -ExpectedEnabledPolicyCount $ExpectedCount
            }
            Write-Output 'PASS'
            if ($null -ne $result) {
                Write-Output ($result | ConvertTo-Json -Depth 8 -Compress)
            }
            """;

        var kind =
            sourceScript.Contains(
                Path.Combine("tools", "ops"),
                StringComparison.OrdinalIgnoreCase)
                ? "operational"
                : "prelive";
        return await RunPowerShellHarnessAsync(
            harness,
            sourceScript,
            kind,
            healthJson,
            readyJson,
            expectedMode,
            expectedCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            allowMissing ? "1" : "0");
    }

    private static Task<PowerShellFixtureResult>
        RunDeployArgumentFixtureAsync(string sourceScript)
    {
        const string harness =
            """
            param([string]$SourceScript)
            $ErrorActionPreference = 'Stop'
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $SourceScript,
                [ref]$tokens,
                [ref]$errors)
            if ($errors.Count -ne 0) {
                throw ($errors | ForEach-Object Message)
            }
            $requiredNames = @(
                'New-ClientCompatibilityGateArguments',
                'New-LinuxPublisherCompatibilityArguments')
            $functions = @(
                $ast.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $requiredNames -contains $node.Name
                    },
                    $true))
            foreach ($name in $requiredNames) {
                $definition = @(
                    $functions |
                        Where-Object Name -eq $name)
                if ($definition.Count -ne 1) {
                    throw "Expected one function '$name', found $($definition.Count)."
                }
                . ([scriptblock]::Create($definition[0].Extent.Text))
            }
            $outer = @(
                New-ClientCompatibilityGateArguments `
                    -ExpectedClientCompatibilityMode StrictBlock `
                    -ExpectedClientCompatibilityEnabledPolicyCount 2 `
                    -AllowMissingClientCompatibilitySummary)
            $publisher = @(
                New-LinuxPublisherCompatibilityArguments `
                    -ExpectedClientCompatibilityMode StrictBlock `
                    -ExpectedClientCompatibilityEnabledPolicyCount 2 `
                    -AllowLegacyPreDeployCompatibilitySummary)
            $rangeRejected = $false
            try {
                [void]@(
                    New-LinuxPublisherCompatibilityArguments `
                        -ExpectedClientCompatibilityMode AuditOnly `
                        -ExpectedClientCompatibilityEnabledPolicyCount 3)
            }
            catch [System.Management.Automation.ParameterBindingException] {
                $rangeRejected = $true
            }
            Write-Output ('outer=' + ($outer -join '|'))
            Write-Output ('publisher=' + ($publisher -join '|'))
            Write-Output ('rangeRejected=' + $rangeRejected)
            """;

        return RunPowerShellHarnessAsync(
            harness,
            sourceScript,
            kind: null,
            healthJson: null,
            readyJson: null,
            expectedMode: null,
            expectedCount: null,
            allowMissing: null);
    }

    private static async Task<PowerShellFixtureResult>
        RunPowerShellHarnessAsync(
            string harness,
            string sourceScript,
            string? kind,
            string? healthJson,
            string? readyJson,
            string? expectedMode,
            string? expectedCount,
            string? allowMissing)
    {
        var fixtureRoot =
            Path.Combine(
                Path.GetTempPath(),
                "georaeplan-compatibility-fixture-" +
                Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        Exception? executionFailure = null;
        try
        {
            var harnessPath =
                Path.Combine(fixtureRoot, "fixture.ps1");
            await File.WriteAllTextAsync(
                harnessPath,
                harness,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var arguments = new List<string>
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                harnessPath,
                sourceScript
            };
            if (kind is not null)
            {
                var healthPath =
                    await WriteJsonFixtureAsync(
                        fixtureRoot,
                        "health.json",
                        healthJson);
                var readyPath =
                    await WriteJsonFixtureAsync(
                        fixtureRoot,
                        "ready.json",
                        readyJson);
                arguments.AddRange(
                [
                    kind,
                    healthPath,
                    readyPath,
                    expectedMode!,
                    expectedCount!,
                    allowMissing!
                ]);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            var result =
                await RedirectedProcessRunner.RunAsync(
                    startInfo,
                    TimeSpan.FromSeconds(20),
                    "Client compatibility PowerShell fixture");
            return new PowerShellFixtureResult(
                result.ExitCode,
                result.StdOut + result.StdErr);
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            throw;
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                try
                {
                    Directory.Delete(fixtureRoot, recursive: true);
                }
                catch (Exception cleanupException)
                {
                    if (executionFailure is null)
                        throw;

                    throw new AggregateException(
                        "The compatibility fixture execution and directory cleanup both failed.",
                        executionFailure,
                        cleanupException);
                }
            }
        }
    }

    private static async Task<string> WriteJsonFixtureAsync(
        string fixtureRoot,
        string fileName,
        string? json)
    {
        if (json is null)
            return "__GEORAEPLAN_NULL_FIXTURE__";

        var path = Path.Combine(fixtureRoot, fileName);
        await File.WriteAllTextAsync(
            path,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private sealed record PowerShellFixtureResult(
        int ExitCode,
        string Output);

    private static void AssertPolicyMapping(
        string compose,
        int index,
        string prefix,
        string appId,
        string platform)
    {
        var policyPrefix =
            $"ClientCompatibility__Policies__{index}__";
        Assert.Contains(
            $"{policyPrefix}Enabled: ${{CLIENT_COMPATIBILITY_{prefix}_ENABLED:-false}}",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{policyPrefix}AppId: ${{CLIENT_COMPATIBILITY_{prefix}_APP_ID:-{appId}}}",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{policyPrefix}Platform: ${{CLIENT_COMPATIBILITY_{prefix}_PLATFORM:-{platform}}}",
            compose,
            StringComparison.Ordinal);

        foreach (var property in new[]
                 {
                     "PolicyVersion",
                     "RequiresUserAction",
                     "MinimumVersion",
                     "MinimumBuild",
                     "MinimumProtocolVersion",
                     "LatestVersion",
                     "LatestBuild",
                     "UpdateUrl",
                     "UpgradeToken"
                 })
        {
            Assert.Contains(
                policyPrefix + property + ":",
                compose,
                StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory =
            new DirectoryInfo(
                Path.GetDirectoryName(sourceFilePath) ??
                AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "거래플랜.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "거래플랜 저장소 루트를 찾지 못했습니다.");
    }

    private static int CountOccurrences(
        string source,
        string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(
                    value,
                    index,
                    StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
