using System.Diagnostics;
using System.Text;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RedirectedProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_TimeoutTerminatesParentAndChildProcesses()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var fixtureRoot =
            Path.Combine(
                Path.GetTempPath(),
                "georaeplan-redirected-process-" +
                Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        Exception? testFailure = null;
        try
        {
            var processIdentityPath =
                Path.Combine(fixtureRoot, "process-identities.txt");
            var childCommand = EncodePowerShell(
                "Start-Sleep -Seconds 30");
            var escapedIdentityPath =
                processIdentityPath.Replace("'", "''", StringComparison.Ordinal);
            var parentCommand =
                $$"""
                $child = Start-Process powershell.exe -ArgumentList @(
                    '-NoProfile',
                    '-NonInteractive',
                    '-EncodedCommand',
                    '{{childCommand}}') -PassThru
                $parent = Get-Process -Id $PID
                $lines = @(
                    ('{0}|{1}|{2}' -f $parent.Id, $parent.StartTime.ToUniversalTime().Ticks, $parent.Path),
                    ('{0}|{1}|{2}' -f $child.Id, $child.StartTime.ToUniversalTime().Ticks, $child.Path))
                [System.IO.File]::WriteAllLines(
                    '{{escapedIdentityPath}}',
                    $lines,
                    [System.Text.UTF8Encoding]::new($false))
                Wait-Process -Id $child.Id
                """;
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(
                EncodePowerShell(parentCommand));

            var exception =
                await Assert.ThrowsAsync<TimeoutException>(
                    () => RedirectedProcessRunner.RunAsync(
                        startInfo,
                        TimeSpan.FromSeconds(3),
                        "Timeout fixture"));

            Assert.Contains(
                "process tree was terminated",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(
                File.Exists(processIdentityPath),
                "The timeout fixture did not record its process identities.");
            foreach (var identity in
                     File.ReadAllLines(processIdentityPath)
                         .Select(ParseOwnedProcessIdentity))
            {
                Assert.False(
                    IsRecordedProcessRunning(identity),
                    $"A timed-out fixture process is still running: {identity}");
            }
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            var ownedProcesses = new List<Process>();
            var processIdentityPath =
                Path.Combine(fixtureRoot, "process-identities.txt");
            if (File.Exists(processIdentityPath))
            {
                foreach (var identityText in
                         File.ReadAllLines(processIdentityPath))
                {
                    try
                    {
                        var identity =
                            ParseOwnedProcessIdentity(identityText);
                        if (TryOpenOwnedProcess(
                                identity,
                                out var process))
                        {
                            ownedProcesses.Add(process);
                        }
                    }
                    catch (Exception cleanupException)
                    {
                        cleanupFailures.Add(cleanupException);
                    }
                }
            }

            foreach (var process in ownedProcesses)
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        if (!process.WaitForExit(
                                (int)TimeSpan
                                    .FromSeconds(5)
                                    .TotalMilliseconds))
                        {
                            cleanupFailures.Add(
                                new TimeoutException(
                                    $"Timed out cleaning owned process {process.Id}."));
                        }
                    }
                    catch (Exception cleanupException)
                    {
                        cleanupFailures.Add(cleanupException);
                    }
                }
            }

            if (Directory.Exists(fixtureRoot))
            {
                try
                {
                    Directory.Delete(fixtureRoot, recursive: true);
                }
                catch (Exception cleanupException)
                {
                    cleanupFailures.Add(cleanupException);
                }
            }

            if (cleanupFailures.Count > 0)
            {
                var failures = new List<Exception>();
                if (testFailure is not null)
                    failures.Add(testFailure);
                failures.AddRange(cleanupFailures);
                throw new AggregateException(
                    "The timeout regression and its fail-safe cleanup did not both complete cleanly.",
                    failures);
            }
        }
    }

    private static string EncodePowerShell(string command) =>
        Convert.ToBase64String(
            Encoding.Unicode.GetBytes(command));

    private static OwnedProcessIdentity ParseOwnedProcessIdentity(
        string identity)
    {
        var fields = identity.Split('|', count: 3);
        if (fields.Length != 3 ||
            !int.TryParse(fields[0], out var processId) ||
            !long.TryParse(fields[1], out var startTicks) ||
            string.IsNullOrWhiteSpace(fields[2]))
        {
            throw new InvalidOperationException(
                $"Owned process identity is invalid: {identity}");
        }

        return new OwnedProcessIdentity(
            processId,
            startTicks,
            Path.GetFullPath(fields[2]));
    }

    private static bool IsRecordedProcessRunning(
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
            candidate =
                Process.GetProcessById(identity.ProcessId);
            var safeHandle = candidate.SafeHandle;
            if (safeHandle.IsInvalid || safeHandle.IsClosed)
            {
                candidate.Dispose();
                process = null!;
                return false;
            }

            candidate.Refresh();
            if (candidate.HasExited ||
                candidate.StartTime.ToUniversalTime().Ticks !=
                    identity.StartTimeUtcTicks ||
                !string.Equals(
                    candidate.MainModule?.FileName,
                    identity.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                candidate.Dispose();
                process = null!;
                return false;
            }

            process = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            candidate?.Dispose();
            process = null!;
            return false;
        }
        catch (InvalidOperationException)
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
}
