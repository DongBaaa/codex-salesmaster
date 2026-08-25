using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SoakObservationGuardTests
{
    [Fact]
    public void BaselineSoakScheduler_IsIndependentFailClosedAndExact()
    {
        var root = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            root,
            "tools",
            "verification",
            "Start-GeoraePlanBaselineSoak.ps1");
        Assert.True(File.Exists(scriptPath), $"Scheduler not found: {scriptPath}");
        var source = File.ReadAllText(scriptPath);

        foreach (var required in new[]
                 {
                     "[ValidateSet('Start', 'Status', 'Cleanup')]",
                     "SampleCount -ne 1440",
                     "IntervalSeconds -ne 60",
                     "BaseUrl must be exactly https://trade.2884.kr.",
                     "The test runtime is explicitly invalid",
                     "ScheduledTaskLogonType = 'Interactive'",
                     "ScheduledTaskRunLevel = 'Limited'",
                     "-ExecutionTimeLimit ([TimeSpan]::Zero)",
                     "New-ScheduledTaskPrincipal",
                     "Register-ScheduledTask",
                     "Start-ScheduledTask",
                     "function Get-FileSha256",
                     "FirstSampleHealthy = $true",
                     "Cleanup before exact baseline completion requires -ForceCleanup.",
                     "EvidencePreserved = [IO.Directory]::Exists($soak)"
                 })
        {
            Assert.Contains(required, source, StringComparison.Ordinal);
        }

        Assert.Contains(
            "60BC0FEC39E8B94E7657AD900F40D941574F68C78C4B03B49DC8FC81C82F1AC0",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Register-ScheduledTask -Force",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Get-FileHash",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Remove-Item -Recurse",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaselineSoakScheduler_ValidateOnlyAcceptsCertifiedFixtureWithoutMutation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"baseline-scheduler-validate-{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(testRoot, "project");
        var runtimeRoot = Path.Combine(projectRoot, "테스트 시행", "실행환경");
        var verificationRoot = Path.Combine(projectRoot, "tools", "verification");
        var runId = $"fixture-{Guid.NewGuid():N}"[..40];
        var soakRoot = Path.Combine(
            @"D:\DevCaches\georaeplan-v1-soak",
            $"soak-{runId}");
        var runAllTaskName = $"GeoraePlan-Soak-{runId}-RunAll";
        var observerTaskName = $"GeoraePlan-Soak-{runId}-Observer";

        Directory.CreateDirectory(Path.Combine(runtimeRoot, "App"));
        Directory.CreateDirectory(verificationRoot);
        try
        {
            File.Copy(
                Path.Combine(
                    repositoryRoot,
                    "tools",
                    "verification",
                    "Invoke-GeoraePlanSoakObservation.ps1"),
                Path.Combine(
                    verificationRoot,
                    "Invoke-GeoraePlanSoakObservation.ps1"));
            File.WriteAllText(
                Path.Combine(runtimeRoot, ".georaeplan-runtime-ready"),
                "fixture-ready");
            File.WriteAllText(
                Path.Combine(runtimeRoot, "Run-All.ps1"),
                "throw 'ValidateOnly must not execute Run-All.'");
            File.WriteAllBytes(
                Path.Combine(runtimeRoot, "App", "거래플랜.Desktop.App.exe"),
                [0x4D, 0x5A, 0x00, 0x00]);
            File.WriteAllBytes(
                Path.Combine(runtimeRoot, "App", "거래플랜.Desktop.App.dll"),
                [0x4D, 0x5A, 0x00, 0x00]);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ResolveWindowsPowerShellPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-NoLogo",
                         "-NoProfile",
                         "-NonInteractive",
                         "-ExecutionPolicy",
                         "Bypass",
                         "-File",
                         Path.Combine(
                             repositoryRoot,
                             "tools",
                             "verification",
                             "Start-GeoraePlanBaselineSoak.ps1"),
                         "-Mode",
                         "Start",
                         "-RunId",
                         runId,
                         "-ProjectRoot",
                         projectRoot,
                         "-SoakRoot",
                         soakRoot,
                         "-ValidateOnly"
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new System.Diagnostics.Process
            {
                StartInfo = startInfo
            };
            Assert.True(process.Start());
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(
                process.ExitCode == 0,
                $"ValidateOnly failed. Exit={process.ExitCode}{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{stderr}");
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
            using var json = System.Text.Json.JsonDocument.Parse(stdout);
            var root = json.RootElement;
            Assert.Equal("PASS", root.GetProperty("Result").GetString());
            Assert.Equal("VALIDATED", root.GetProperty("Mode").GetString());
            Assert.Equal(runId, root.GetProperty("RunId").GetString());
            Assert.Equal(
                "Interactive",
                root.GetProperty("ScheduledTaskLogonType").GetString());
            Assert.Equal(
                "Limited",
                root.GetProperty("ScheduledTaskRunLevel").GetString());
            Assert.False(Directory.Exists(soakRoot));
            Assert.False(ScheduledTaskExists(runAllTaskName));
            Assert.False(ScheduledTaskExists(observerTaskName));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void SoakObservation_IsReadOnlyAndPersistsIncrementalEvidence()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repoRoot,
            "tools",
            "verification",
            "Invoke-GeoraePlanSoakObservation.ps1");
        var source = File.ReadAllText(scriptPath);

        Assert.Contains("[int]$SampleCount = 1440", source, StringComparison.Ordinal);
        Assert.Contains("[int]$IntervalSeconds = 60", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireDesktopProcess", source, StringComparison.Ordinal);
        Assert.Contains("/healthz", source, StringComparison.Ordinal);
        Assert.Contains("/updates/manifest?channel=", source, StringComparison.Ordinal);
        Assert.Contains(
            "function Get-OptionalManifestVersion",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$Manifest.PSObject.Properties[$PackageName]",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$manifestJson.desktop.version",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$manifestJson.android.version",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Get-DesktopProcessSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("WorkingSetMb", source, StringComparison.Ordinal);
        Assert.Contains("Responding", source, StringComparison.Ordinal);
        Assert.Contains("AppendAllText($csvPath", source, StringComparison.Ordinal);
        Assert.Contains("soak_observation_report=", source, StringComparison.Ordinal);
        Assert.Contains("운영 데이터 생성, 수정, 삭제 API는 호출하지 않습니다.", source, StringComparison.Ordinal);

        Assert.DoesNotContain("-Method Post", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Put", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Patch", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Delete", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedRunAll_ContinuouslyObservesServerWithBoundedReadOnlyLogs()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repoRoot,
            "테스트 시행",
            "테스트-환경-준비.ps1");
        var source = File.ReadAllText(scriptPath);
        var healthProbeStart = source.IndexOf(
            "function Invoke-RuntimeHealthProbe",
            StringComparison.Ordinal);
        var healthProbeEnd = source.IndexOf(
            "function Remove-OldRuntimeServerLogs",
            healthProbeStart,
            StringComparison.Ordinal);

        Assert.True(healthProbeStart >= 0);
        Assert.True(healthProbeEnd > healthProbeStart);
        var healthProbeSource = source[healthProbeStart..healthProbeEnd];
        Assert.Contains("-Method Get", healthProbeSource, StringComparison.Ordinal);
        Assert.Contains("-TimeoutSec 1", healthProbeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("-Method Post", healthProbeSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Put", healthProbeSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Patch", healthProbeSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Delete", healthProbeSource, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("RuntimeLogs", source, StringComparison.Ordinal);
        Assert.Contains("health-observation.csv", source, StringComparison.Ordinal);
        Assert.Contains("health-observation.previous.csv", source, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::AppendAllText(", source, StringComparison.Ordinal);
        Assert.Contains("-MaximumSamplesPerFile 17280", source, StringComparison.Ordinal);
        Assert.Contains("$appProcess.WaitForExit(250)", source, StringComparison.Ordinal);
        Assert.Contains("$consecutiveHealthFailures -ge 3", source, StringComparison.Ordinal);
        Assert.Contains("Stop-RuntimeAppAfterServerFailure", source, StringComparison.Ordinal);
        Assert.Contains("MaximumBytesPerFile = 67108864", source, StringComparison.Ordinal);
        Assert.Contains("MaximumTotalBytes = 134217728", source, StringComparison.Ordinal);
        Assert.Contains("MaximumTotalBytes = 268435456", source, StringComparison.Ordinal);
        Assert.Contains(
            "Runtime server logs exceeded their total safety limit.",
            source,
            StringComparison.Ordinal);
        var serverRetryStart = source.IndexOf(
            "for ($attempt = 1; $attempt -le 10; $attempt++) {",
            StringComparison.Ordinal);
        var serverProcessStart = source.IndexOf(
            "$serverProcess = Start-HiddenServerProcess",
            serverRetryStart,
            StringComparison.Ordinal);
        Assert.True(serverRetryStart >= 0);
        Assert.True(serverProcessStart > serverRetryStart);
        Assert.Contains(
            "Remove-OldRuntimeServerLogs -LogRoot $runtimeLogRoot",
            source[serverRetryStart..serverProcessStart],
            StringComparison.Ordinal);
        var serverLauncherStart = source.IndexOf(
            "function Start-HiddenServerProcess",
            StringComparison.Ordinal);
        var serverLauncherEnd = source.IndexOf(
            "$dotnetExe = '__DOTNET_EXE__'",
            serverLauncherStart,
            StringComparison.Ordinal);
        Assert.True(serverLauncherStart >= 0);
        Assert.True(serverLauncherEnd > serverLauncherStart);
        Assert.Contains(
            "'Logging__LogLevel__Microsoft.AspNetCore' = 'Warning'",
            source[serverLauncherStart..serverLauncherEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command' = 'Warning'",
            source[serverLauncherStart..serverLauncherEnd],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Wait-Process -Id $appProcess.Id",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SoakObservation_IsUtf8BomForWindowsPowerShellCompatibility()
    {
        var repoRoot = FindRepositoryRoot();
        foreach (var fileName in new[]
                 {
                     "Invoke-GeoraePlanSoakObservation.ps1",
                     "Start-GeoraePlanBaselineSoak.ps1"
                 })
        {
            var scriptPath = Path.Combine(
                repoRoot,
                "tools",
                "verification",
                fileName);
            var bytes = File.ReadAllBytes(scriptPath);

            Assert.True(bytes.Length > 3);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
        }
    }

    [Fact]
    public void PaidDeliveryGate_CanRequireFreshPassingSoakEvidence()
    {
        var repoRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "verification",
            "Invoke-GeoraePlanPaidDeliveryGate.ps1"));

        Assert.Contains("[string]$SoakEvidencePath", source, StringComparison.Ordinal);
        Assert.Contains("[int]$MaxSoakEvidenceAgeHours = 168", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireSoakPass", source, StringComparison.Ordinal);
        Assert.Contains("soak-observation-evidence", source, StringComparison.Ordinal);
        Assert.Contains("RequireSoakPass was specified, but SoakEvidencePath is empty.", source, StringComparison.Ordinal);
        Assert.Contains("$soakStatus -ne 'PASS'", source, StringComparison.Ordinal);
        Assert.Contains("$soakAge.TotalHours -gt $MaxSoakEvidenceAgeHours", source, StringComparison.Ordinal);
        Assert.Contains("장시간 관찰 PASS 증거를 확인했습니다", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "거래플랜.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }

    private static string ResolveWindowsPowerShellPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

    private static bool ScheduledTaskExists(string taskName)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ResolveWindowsPowerShellPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-NoLogo",
                     "-NoProfile",
                     "-NonInteractive",
                     "-Command",
                     $"if (Get-ScheduledTask -TaskName '{taskName}' -ErrorAction SilentlyContinue) {{ exit 0 }} else {{ exit 1 }}"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process
        {
            StartInfo = startInfo
        };
        Assert.True(process.Start());
        process.WaitForExit(10_000);
        return process.ExitCode == 0;
    }
}
