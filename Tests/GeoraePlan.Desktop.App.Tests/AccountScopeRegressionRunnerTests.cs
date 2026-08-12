using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AccountScopeRegressionRunnerTests
{
    [Fact]
    public async Task AssertTestFiltersMatched_FailsWhenAnyFilterMatchesNoTest()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"account-scope-filter-zero-{Guid.NewGuid():N}");
        var trxPath = Path.Combine(testRoot, "results.trx");
        var harness = Path.Combine(testRoot, "assert-filters.ps1");
        Directory.CreateDirectory(testRoot);

        try
        {
            var utf8NoBom =
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(
                trxPath,
                """
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <TestDefinitions>
                    <UnitTest>
                      <TestMethod
                        className="GeoraePlan.Server.Api.Tests.OfficeScopeAndPagingTests"
                        name="ReturnsScopedRows" />
                    </UnitTest>
                  </TestDefinitions>
                </TestRun>
                """,
                utf8NoBom);
            File.WriteAllText(
                harness,
                CreateFunctionHarness(
                    "Assert-TestFiltersMatched",
                    """
                    Assert-TestFiltersMatched `
                        -TrxPath $Args[0] `
                        -FilterParts @(
                            'FullyQualifiedName~OfficeScopeAndPagingTests',
                            'FullyQualifiedName~MissingScopeTest'
                        )
                    """),
                utf8NoBom);

            var result = await RunPowerShellAsync(
                harness,
                TimeSpan.FromSeconds(30),
                ResolveRunnerScript(),
                trxPath);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "FullyQualifiedName~MissingScopeTest",
                result.Stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AssertTestFiltersMatched_AcceptsEveryMatchedFilter()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"account-scope-filter-matches-{Guid.NewGuid():N}");
        var trxPath = Path.Combine(testRoot, "results.trx");
        var harness = Path.Combine(testRoot, "assert-filters.ps1");
        Directory.CreateDirectory(testRoot);

        try
        {
            var utf8NoBom =
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(
                trxPath,
                """
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <TestDefinitions>
                    <UnitTest>
                      <TestMethod
                        className="GeoraePlan.Server.Api.Tests.OfficeScopeAndPagingTests"
                        name="ReturnsScopedRows" />
                    </UnitTest>
                    <UnitTest>
                      <TestMethod
                        className="GeoraePlan.Server.Api.Tests.SyncControllerTests"
                        name="Push_SkipsOutOfScopeWarehouseStock_AndReportsNotice" />
                    </UnitTest>
                  </TestDefinitions>
                </TestRun>
                """,
                utf8NoBom);
            File.WriteAllText(
                harness,
                CreateFunctionHarness(
                    "Assert-TestFiltersMatched",
                    """
                    Assert-TestFiltersMatched `
                        -TrxPath $Args[0] `
                        -FilterParts @(
                            'FullyQualifiedName~OfficeScopeAndPagingTests',
                            'FullyQualifiedName~SyncControllerTests.Push_SkipsOutOfScopeWarehouseStock_AndReportsNotice'
                        )
                    """),
                utf8NoBom);

            var result = await RunPowerShellAsync(
                harness,
                TimeSpan.FromSeconds(30),
                ResolveRunnerScript(),
                trxPath);

            Assert.True(
                result.ExitCode == 0,
                "The filter match probe failed." +
                Environment.NewLine +
                "STDOUT:" + Environment.NewLine +
                result.Stdout +
                Environment.NewLine +
                "STDERR:" + Environment.NewLine +
                result.Stderr);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveDotnetCommand_PrefersEnvironmentOverrideAndValidatesFromProjectRoot()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"account-scope-dotnet-resolution-{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(testRoot, "project root");
        var toolingRoot = Path.Combine(testRoot, "tooling");
        var fakeDotnet = Path.Combine(toolingRoot, "dotnet.cmd");
        var cwdLog = Path.Combine(testRoot, "dotnet-cwd.txt");
        var harness = Path.Combine(testRoot, "invoke-resolution.ps1");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(toolingRoot);

        try
        {
            var utf8NoBom =
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(
                fakeDotnet,
                """
                @echo off
                chcp 65001 >nul
                if /I not "%~1"=="--version" exit /b 91
                >"%DOTNET_CWD_LOG%" echo %CD%
                echo 8.0.999
                exit /b 0
                """,
                Encoding.ASCII);
            File.WriteAllText(
                harness,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$ProjectRoot,
                    [Parameter(Mandatory = $true)][string]$FakeDotnet,
                    [Parameter(Mandatory = $true)][string]$CwdLog
                )

                $ErrorActionPreference = 'Stop'
                [Console]::OutputEncoding =
                    [System.Text.UTF8Encoding]::new($false)
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join [Environment]::NewLine)
                }

                $functionAst = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq 'Resolve-DotnetCommand'
                }, $true)
                if ($null -eq $functionAst) {
                    throw 'Resolve-DotnetCommand function was not found.'
                }

                . ([scriptblock]::Create($functionAst.Extent.Text))
                $env:DOTNET_EXE = $FakeDotnet
                $env:DOTNET_CWD_LOG = $CwdLog
                Resolve-DotnetCommand -ProjectRoot $ProjectRoot
                """,
                utf8NoBom);

            var result = await RunPowerShellAsync(
                harness,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolveRunnerScript(),
                "-ProjectRoot",
                projectRoot,
                "-FakeDotnet",
                fakeDotnet,
                "-CwdLog",
                cwdLog);

            Assert.True(
                result.ExitCode == 0,
                "The dotnet resolver probe failed." +
                Environment.NewLine +
                "STDOUT:" + Environment.NewLine +
                result.Stdout +
                Environment.NewLine +
                "STDERR:" + Environment.NewLine +
                result.Stderr);
            Assert.Equal(
                Path.GetFullPath(fakeDotnet),
                Path.GetFullPath(result.Stdout.Trim()),
                ignoreCase: true);
            Assert.True(
                File.Exists(cwdLog),
                "The environment-provided dotnet candidate was not invoked.");
            Assert.Equal(
                Path.GetFullPath(projectRoot),
                Path.GetFullPath(File.ReadAllText(cwdLog).Trim()),
                ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveDotnetCommand_SkipsEnvironmentOverrideWithIncompatibleSdkMajor()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"account-scope-dotnet-major-{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(testRoot, "project");
        var toolingRoot = Path.Combine(testRoot, "tooling");
        var incompatibleDotnet = Path.Combine(
            toolingRoot,
            "dotnet.cmd");
        var invocationMarker = Path.Combine(
            testRoot,
            "incompatible-invoked.txt");
        var harness = Path.Combine(
            testRoot,
            "invoke-resolution.ps1");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(toolingRoot);

        try
        {
            var utf8NoBom =
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(
                Path.Combine(projectRoot, "global.json"),
                """
                {
                  "sdk": {
                    "version": "8.0.421",
                    "rollForward": "latestFeature"
                  }
                }
                """,
                utf8NoBom);
            File.WriteAllText(
                incompatibleDotnet,
                """
                @echo off
                if /I not "%~1"=="--version" exit /b 91
                >"%INCOMPATIBLE_DOTNET_MARKER%" echo invoked
                echo 6.0.999
                exit /b 0
                """,
                Encoding.ASCII);
            File.WriteAllText(
                harness,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$ProjectRoot,
                    [Parameter(Mandatory = $true)][string]$FakeDotnet,
                    [Parameter(Mandatory = $true)][string]$Marker
                )

                $ErrorActionPreference = 'Stop'
                [Console]::OutputEncoding =
                    [System.Text.UTF8Encoding]::new($false)
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join [Environment]::NewLine)
                }

                $functionAst = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq 'Resolve-DotnetCommand'
                }, $true)
                if ($null -eq $functionAst) {
                    throw 'Resolve-DotnetCommand function was not found.'
                }

                . ([scriptblock]::Create($functionAst.Extent.Text))
                $env:DOTNET_EXE = $FakeDotnet
                $env:INCOMPATIBLE_DOTNET_MARKER = $Marker
                Resolve-DotnetCommand -ProjectRoot $ProjectRoot
                """,
                utf8NoBom);

            var result = await RunPowerShellAsync(
                harness,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolveRunnerScript(),
                "-ProjectRoot",
                projectRoot,
                "-FakeDotnet",
                incompatibleDotnet,
                "-Marker",
                invocationMarker);

            Assert.True(
                result.ExitCode == 0,
                "The dotnet major-version fallback probe failed." +
                Environment.NewLine +
                "STDOUT:" + Environment.NewLine +
                result.Stdout +
                Environment.NewLine +
                "STDERR:" + Environment.NewLine +
                result.Stderr);
            Assert.True(
                File.Exists(invocationMarker),
                "The incompatible environment override was not evaluated.");
            Assert.False(
                string.Equals(
                    Path.GetFullPath(incompatibleDotnet),
                    Path.GetFullPath(result.Stdout.Trim()),
                    StringComparison.OrdinalIgnoreCase),
                "The resolver selected an incompatible SDK major.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveDotnetCommand_SkipsPathCandidateBelowNet8AndUsesNextSdk8Candidate()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"account-scope-dotnet-path-major-{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(testRoot, "project");
        var incompatibleToolingRoot = Path.Combine(
            testRoot,
            "incompatible tooling");
        var compatibleToolingRoot = Path.Combine(
            testRoot,
            "compatible tooling");
        var incompatibleDotnet = Path.Combine(
            incompatibleToolingRoot,
            "dotnet.cmd");
        var compatibleDotnet = Path.Combine(
            compatibleToolingRoot,
            "dotnet.cmd");
        var incompatibleMarker = Path.Combine(
            testRoot,
            "incompatible-invoked.txt");
        var compatibleMarker = Path.Combine(
            testRoot,
            "compatible-invoked.txt");
        var harness = Path.Combine(
            testRoot,
            "invoke-resolution.ps1");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(incompatibleToolingRoot);
        Directory.CreateDirectory(compatibleToolingRoot);

        try
        {
            var utf8NoBom =
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(
                incompatibleDotnet,
                """
                @echo off
                if /I not "%~1"=="--version" exit /b 91
                >"%INCOMPATIBLE_DOTNET_MARKER%" echo invoked
                echo 7.0.999
                exit /b 0
                """,
                Encoding.ASCII);
            File.WriteAllText(
                compatibleDotnet,
                """
                @echo off
                if /I not "%~1"=="--version" exit /b 91
                >"%COMPATIBLE_DOTNET_MARKER%" echo invoked
                echo 8.0.999
                exit /b 0
                """,
                Encoding.ASCII);
            File.WriteAllText(
                harness,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$ProjectRoot,
                    [Parameter(Mandatory = $true)][string]$PathCandidateRoot,
                    [Parameter(Mandatory = $true)][string]$FallbackDotnet,
                    [Parameter(Mandatory = $true)][string]$IncompatibleMarker,
                    [Parameter(Mandatory = $true)][string]$CompatibleMarker
                )

                $ErrorActionPreference = 'Stop'
                [Console]::OutputEncoding =
                    [System.Text.UTF8Encoding]::new($false)
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join [Environment]::NewLine)
                }

                $functionAst = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq 'Resolve-DotnetCommand'
                }, $true)
                if ($null -eq $functionAst) {
                    throw 'Resolve-DotnetCommand function was not found.'
                }

                . ([scriptblock]::Create($functionAst.Extent.Text))
                $env:DOTNET_EXE = $null
                $env:INCOMPATIBLE_DOTNET_MARKER = $IncompatibleMarker
                $env:COMPATIBLE_DOTNET_MARKER = $CompatibleMarker
                $env:PATH = $PathCandidateRoot
                Resolve-DotnetCommand `
                    -ProjectRoot $ProjectRoot `
                    -FallbackCandidates @($FallbackDotnet)
                """,
                utf8NoBom);

            var result = await RunPowerShellAsync(
                harness,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolveRunnerScript(),
                "-ProjectRoot",
                projectRoot,
                "-PathCandidateRoot",
                incompatibleToolingRoot,
                "-FallbackDotnet",
                compatibleDotnet,
                "-IncompatibleMarker",
                incompatibleMarker,
                "-CompatibleMarker",
                compatibleMarker);

            Assert.True(
                result.ExitCode == 0,
                "The PATH dotnet major-version fallback probe failed." +
                Environment.NewLine +
                "STDOUT:" + Environment.NewLine +
                result.Stdout +
                Environment.NewLine +
                "STDERR:" + Environment.NewLine +
                result.Stderr);
            Assert.Equal(
                Path.GetFullPath(compatibleDotnet),
                Path.GetFullPath(result.Stdout.Trim()),
                ignoreCase: true);
            Assert.True(
                File.Exists(incompatibleMarker),
                "The incompatible PATH candidate was not evaluated.");
            Assert.True(
                File.Exists(compatibleMarker),
                "The compatible SDK 8 fallback was not evaluated.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string ResolveRunnerScript()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "tasks",
            "Run-OptionalAccountScopeRegression.ps1");
        Assert.True(File.Exists(path), $"Runner script not found: {path}");
        return path;
    }

    private static string CreateFunctionHarness(
        string functionName,
        string invocation)
    {
        return $$"""
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][string]$SourceScript,
                [Parameter(ValueFromRemainingArguments = $true)]
                [string[]]$FunctionArguments
            )

            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding =
                [System.Text.UTF8Encoding]::new($false)
            $tokens = $null
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $SourceScript,
                [ref]$tokens,
                [ref]$parseErrors)
            if ($parseErrors.Count -ne 0) {
                throw (($parseErrors | ForEach-Object Message) -join [Environment]::NewLine)
            }

            $functionAst = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq '{{functionName}}'
            }, $true)
            if ($null -eq $functionAst) {
                throw '{{functionName}} function was not found.'
            }

            . ([scriptblock]::Create($functionAst.Extent.Text))
            $Args = $FunctionArguments
            {{invocation}}
            """;
    }

    private static async Task<PowerShellResult> RunPowerShellAsync(
        string scriptPath,
        TimeSpan timeout,
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
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows PowerShell did not start.");
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
            Path.GetDirectoryName(sourceFilePath) ??
            AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (
                Directory.Exists(
                    Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(
                    Path.Combine(directory.FullName, "Desktop")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Repository root was not found.");
    }

    private sealed record PowerShellResult(
        int ExitCode,
        string Stdout,
        string Stderr);
}
