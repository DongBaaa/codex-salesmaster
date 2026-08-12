using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class OperationalBackupStatusValidationTests
{
    [Fact]
    public async Task Helper_AcceptsFreshCompleteSetWithMatchingManifest()
    {
        using var fixture = new BackupStatusFixture();

        var result = await RunValidatorAsync(fixture);

        Assert.True(
            result.Status == "PASS",
            $"status={result.Status}; reason={result.Reason}; detail={result.Detail}");
        Assert.Equal("backup_verified", result.Reason);
        Assert.Contains("files=1", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Helper_RejectsStaleCompletedBackup()
    {
        using var fixture = new BackupStatusFixture();
        fixture.WriteSuccessStatus(DateTimeOffset.UtcNow.AddHours(-37));

        var result = await RunValidatorAsync(fixture);

        Assert.Equal("FAIL", result.Status);
        Assert.Equal("backup_status_stale", result.Reason);
    }

    [Fact]
    public async Task Helper_RejectsStatusManifestHashMismatch()
    {
        using var fixture = new BackupStatusFixture();
        fixture.WriteSuccessStatus(
            DateTimeOffset.UtcNow.AddHours(-1),
            new string('0', 64));

        var result = await RunValidatorAsync(fixture);

        Assert.Equal("FAIL", result.Status);
        Assert.Equal("manifest_hash_mismatch", result.Reason);
    }

    [Fact]
    public async Task Helper_RejectsManifestEntryFileHashMismatch()
    {
        using var fixture = new BackupStatusFixture();
        File.WriteAllText(
            fixture.PayloadPath,
            "mutated after manifest publication",
            BackupStatusFixture.Utf8NoBom);

        var result = await RunValidatorAsync(fixture);

        Assert.Equal("FAIL", result.Status);
        Assert.True(
            result.Reason == "manifest_file_hash_mismatch",
            $"reason={result.Reason}; detail={result.Detail}");
    }

    [Fact]
    public async Task Helper_RejectsManifestPathEscapeEvenWhenOutsideHashMatches()
    {
        using var fixture = new BackupStatusFixture();
        var outsidePath = Path.Combine(
            Directory.GetParent(fixture.SetDirectory)!.FullName,
            "outside.txt");
        File.WriteAllText(
            outsidePath,
            "must never be accepted through dot-dot",
            BackupStatusFixture.Utf8NoBom);
        fixture.WriteManifest((GetSha256(outsidePath), "../outside.txt"));
        fixture.WriteSuccessStatus(DateTimeOffset.UtcNow.AddHours(-1));

        var result = await RunValidatorAsync(fixture);

        Assert.Equal("FAIL", result.Status);
        Assert.Equal("manifest_path_escape", result.Reason);
    }

    [Fact]
    public async Task Helper_RejectsFailureStatusNewerThanLastSuccess()
    {
        using var fixture = new BackupStatusFixture();
        var completedAt = DateTimeOffset.UtcNow.AddHours(-2);
        fixture.WriteSuccessStatus(completedAt);
        fixture.WriteFailureStatus(completedAt.AddHours(1));

        var result = await RunValidatorAsync(fixture);

        Assert.Equal("FAIL", result.Status);
        Assert.True(
            result.Reason == "newer_failure_status",
            $"reason={result.Reason}; detail={result.Detail}");
    }

    [Fact]
    public async Task Helper_RejectsDuplicateKeyInsteadOfSubstringMatching()
    {
        using var fixture = new BackupStatusFixture();
        var manifestHash = GetSha256(fixture.ManifestPath);
        File.WriteAllText(
            fixture.SuccessStatusPath,
            string.Join(
                '\n',
                "backup=ok",
                "backup=ok",
                $"completed_at={DateTimeOffset.UtcNow.AddHours(-1):O}",
                $"set_path=/srv/georaeplan/backups/automatic/sets/{fixture.SetName}",
                $"manifest_sha256={manifestHash}",
                string.Empty),
            BackupStatusFixture.Utf8NoBom);

        var result = await RunValidatorAsync(fixture);

        Assert.Equal("FAIL", result.Status);
        Assert.Equal("backup_status_invalid", result.Reason);
    }

    [Fact]
    public void OperationalGate_UsesDedicatedBackupIntegrityCheckAndKeepsReplicaIndependent()
    {
        var operationalGate = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "tools",
                "ops",
                "Invoke-GeoraePlanOperationalGate.ps1"));

        Assert.Contains(
            "Test-GeoraePlanBackupStatus.ps1",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Name 'backup state integrity'",
            operationalGate,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$backup -match 'backup=ok'",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "$replica -match 'replica=ok'",
            operationalGate,
            StringComparison.Ordinal);
    }

    private static async Task<ValidationResult> RunValidatorAsync(
        BackupStatusFixture fixture)
    {
        var scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "ops",
            "Test-GeoraePlanBackupStatus.ps1");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = GetWindowsPowerShellPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add("-PlatformStateRoot");
        process.StartInfo.ArgumentList.Add(fixture.StateRoot);
        process.StartInfo.ArgumentList.Add("-OutputFormat");
        process.StartInfo.ArgumentList.Add("Json");

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Backup validator timed out for fixture {fixture.Root}.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.True(
            process.ExitCode == 0,
            $"Backup validator process failed with exit={process.ExitCode}.{Environment.NewLine}" +
            $"stdout={stdout}{Environment.NewLine}stderr={stderr}");

        var jsonLine = stdout
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        Assert.False(
            string.IsNullOrWhiteSpace(jsonLine),
            $"Backup validator returned no JSON. stderr={stderr}");

        var result = JsonSerializer.Deserialize<ValidationResult>(
            jsonLine,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        return Assert.IsType<ValidationResult>(result);
    }

    private static string GetWindowsPowerShellPath()
    {
        var systemDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.System);
        var executablePath = Path.Combine(
            systemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(
            File.Exists(executablePath),
            $"Windows PowerShell was not found: {executablePath}");
        return executablePath;
    }

    private static string GetSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        var searchRoots = new[]
        {
            Environment.GetEnvironmentVariable("GEORAEPLAN_REPOSITORY_ROOT"),
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };
        foreach (var searchRoot in searchRoots
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var current = new DirectoryInfo(Path.GetFullPath(searchRoot!));
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                    Directory.Exists(Path.Combine(current.FullName, "tools")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "거래플랜 저장소 루트를 찾지 못했습니다.");
    }

    private sealed class BackupStatusFixture : IDisposable
    {
        internal static readonly UTF8Encoding Utf8NoBom =
            new(encoderShouldEmitUTF8Identifier: false);

        internal BackupStatusFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "georaeplan-backup-status-tests",
                Guid.NewGuid().ToString("N"));
            StateRoot = Path.Combine(Root, "ops", "state");
            SetName = "backup_20260728T010203Z-123.complete";
            SetDirectory = Path.Combine(
                Root,
                "backups",
                "automatic",
                "sets",
                SetName);
            PayloadPath = Path.Combine(SetDirectory, "metadata.txt");
            ManifestPath = Path.Combine(SetDirectory, "SHA256SUMS");
            SuccessStatusPath = Path.Combine(
                StateRoot,
                "backup-status.txt");

            Directory.CreateDirectory(StateRoot);
            Directory.CreateDirectory(SetDirectory);
            File.WriteAllText(
                PayloadPath,
                "fixture payload",
                Utf8NoBom);
            File.WriteAllText(
                Path.Combine(SetDirectory, "COMPLETE"),
                "backup=complete\n",
                Utf8NoBom);
            WriteManifest((GetSha256(PayloadPath), "metadata.txt"));
            WriteSuccessStatus(DateTimeOffset.UtcNow.AddHours(-1));
        }

        internal string Root { get; }

        internal string StateRoot { get; }

        internal string SetName { get; }

        internal string SetDirectory { get; }

        internal string PayloadPath { get; }

        internal string ManifestPath { get; }

        internal string SuccessStatusPath { get; }

        internal void WriteManifest(
            params (string Sha256, string RelativePath)[] entries)
        {
            var content = string.Join(
                '\n',
                entries.Select(entry =>
                    $"{entry.Sha256}  {entry.RelativePath}")) + "\n";
            File.WriteAllText(ManifestPath, content, Utf8NoBom);
        }

        internal void WriteSuccessStatus(
            DateTimeOffset completedAt,
            string? manifestHash = null)
        {
            manifestHash ??= GetSha256(ManifestPath);
            var content = string.Join(
                '\n',
                "backup=ok",
                "replica=disabled",
                "run_id=fixture",
                $"completed_at={completedAt:O}",
                $"set_path=/srv/georaeplan/backups/automatic/sets/{SetName}",
                $"manifest_sha256={manifestHash}",
                string.Empty);
            File.WriteAllText(
                SuccessStatusPath,
                content,
                Utf8NoBom);
        }

        internal void WriteFailureStatus(DateTimeOffset failedAt)
        {
            var content = string.Join(
                '\n',
                "backup=failed",
                "replica=disabled",
                "run_id=failure-fixture",
                $"failed_at={failedAt:O}",
                "exit_code=1",
                string.Empty);
            File.WriteAllText(
                Path.Combine(StateRoot, "backup-failure-status.txt"),
                content,
                Utf8NoBom);
        }

        public void Dispose()
        {
            var fixtureRoot = Path.GetFullPath(Root);
            var approvedParent = Path.GetFullPath(
                    Path.Combine(
                        Path.GetTempPath(),
                        "georaeplan-backup-status-tests"))
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!fixtureRoot.StartsWith(
                    approvedParent,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to remove unexpected fixture path: {fixtureRoot}");
            }

            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private sealed record ValidationResult(
        string Status,
        string Reason,
        string Detail,
        string CompletedAt,
        string SetPath,
        string LocalSetPath,
        string ManifestSha256);
}
