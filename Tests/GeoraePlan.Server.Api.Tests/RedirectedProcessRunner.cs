using System.Diagnostics;

namespace GeoraePlan.Server.Api.Tests;

internal static class RedirectedProcessRunner
{
    private static readonly TimeSpan CleanupTimeout =
        TimeSpan.FromSeconds(10);

    public static async Task<RedirectedProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        string description)
    {
        using var process =
            Process.Start(startInfo) ??
            throw new InvalidOperationException(
                $"{description} process did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var completionTask =
            Task.WhenAll(
                process.WaitForExitAsync(),
                stdoutTask,
                stderrTask);

        try
        {
            await completionTask.WaitAsync(timeout);
        }
        catch (TimeoutException timeoutException)
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
            catch (Exception cleanupException)
            {
                cleanupFailures.Add(cleanupException);
            }

            try
            {
                await completionTask.WaitAsync(CleanupTimeout);
            }
            catch (Exception cleanupException)
            {
                cleanupFailures.Add(cleanupException);
            }

            var message =
                $"{description} timed out after {timeout}. " +
                "The process tree was terminated and redirected output was drained.";
            if (cleanupFailures.Count == 0)
                throw new TimeoutException(message, timeoutException);

            var failures = new List<Exception> { timeoutException };
            failures.AddRange(cleanupFailures);
            throw new AggregateException(
                message + " One or more cleanup steps also failed.",
                failures);
        }

        return new RedirectedProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }
}

internal sealed record RedirectedProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr);
