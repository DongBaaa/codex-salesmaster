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
    public void OperationalGate_UsesDedicatedBackupReplicaAndRestoreDrillIntegrityChecks()
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
            "Test-GeoraePlanExternalReplicaStatus.ps1",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Name 'external replica integrity'",
            operationalGate,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$replica -match 'replica=ok'",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "$replicaIntegrityPassed",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-GeoraePlanBackupRestoreDrillStatus.ps1",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Name 'backup restore drill integrity'",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "$restoreDrillIntegrityPassed",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "$replicaIntegrityPassed -and $restoreDrillIntegrityPassed",
            operationalGate,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplicaHelper_RequiresExactBindingToCurrentBackup()
    {
        using var fixture = new ReplicaStatusFixture();

        var pass = await RunReplicaValidatorAsync(fixture);
        Assert.True(
            pass.Status == "PASS",
            $"status={pass.Status}; reason={pass.Reason}; detail={pass.Detail}");
        Assert.Equal("replica_verified", pass.Reason);

        fixture.WriteReplicaStatus(sourceRunId: "20260812T041837Z-70000");
        var wrongRun = await RunReplicaValidatorAsync(fixture);
        Assert.Equal("FAIL", wrongRun.Status);
        Assert.Equal("replica_source_mismatch", wrongRun.Reason);

        fixture.WriteReplicaStatus(sourceManifestSha256: new string('b', 64));
        var wrongManifest = await RunReplicaValidatorAsync(fixture);
        Assert.Equal("FAIL", wrongManifest.Status);
        Assert.Equal("replica_source_mismatch", wrongManifest.Reason);
    }

    [Fact]
    public async Task ReplicaHelper_RejectsExtraKeysStaleStatusAndNewerFailure()
    {
        using var fixture = new ReplicaStatusFixture();
        fixture.WriteReplicaStatus(extraLine: "forged=ok");
        var extra = await RunReplicaValidatorAsync(fixture);
        Assert.Equal("replica_status_invalid", extra.Reason);

        fixture.WriteReplicaStatus(verifiedAt: DateTimeOffset.UtcNow.AddHours(-37));
        var stale = await RunReplicaValidatorAsync(fixture);
        Assert.Equal("replica_status_stale", stale.Reason);

        var verifiedAt = DateTimeOffset.UtcNow.AddHours(-1);
        fixture.WriteReplicaStatus(verifiedAt: verifiedAt);
        fixture.WriteFailureStatus(verifiedAt.AddMinutes(10));
        var newerFailure = await RunReplicaValidatorAsync(fixture);
        Assert.Equal("newer_replica_failure", newerFailure.Reason);
    }

    private static Task<ValidationResult> RunReplicaValidatorAsync(
        ReplicaStatusFixture fixture)
        => RunValidatorProcessAsync(
            Path.Combine(
                FindRepositoryRoot(),
                "tools",
                "ops",
                "Test-GeoraePlanExternalReplicaStatus.ps1"),
            fixture.StateRoot);

    private static async Task<ValidationResult> RunValidatorAsync(
        BackupStatusFixture fixture)
    {
        var scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "ops",
            "Test-GeoraePlanBackupStatus.ps1");
        return await RunValidatorProcessAsync(scriptPath, fixture.StateRoot);
    }

    private static async Task<ValidationResult> RunValidatorProcessAsync(
        string scriptPath,
        string stateRoot)
    {
        var windowsPowerShellPath = GetWindowsPowerShellPath();
        var windowsPowerShellHome = Path.GetDirectoryName(windowsPowerShellPath)
            ?? throw new InvalidOperationException(
                "The Windows PowerShell home directory was not found.");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = windowsPowerShellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.Environment["PSModulePath"] = Path.Combine(
            windowsPowerShellHome,
            "Modules");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add("-PlatformStateRoot");
        process.StartInfo.ArgumentList.Add(stateRoot);
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
                $"Backup validator timed out for state root {stateRoot}.");
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

    private sealed class ReplicaStatusFixture : IDisposable
    {
        internal static readonly UTF8Encoding Utf8NoBom =
            new(encoderShouldEmitUTF8Identifier: false);
        internal const string RunId = "20260812T041836Z-62161";
        internal const string SourceManifest =
            "28a329536278002eb8fa4ca66b45d4b6c06d0ff7ce0a1453bf69a4bfb6f69dc6";

        internal ReplicaStatusFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "georaeplan-replica-status-tests",
                Guid.NewGuid().ToString("N"));
            StateRoot = Path.Combine(Root, "ops", "state");
            Directory.CreateDirectory(StateRoot);
            var completedAt = DateTimeOffset.UtcNow.AddHours(-40);
            File.WriteAllText(
                Path.Combine(StateRoot, "backup-status.txt"),
                string.Join(
                    '\n',
                    "backup=ok",
                    "replica=disabled",
                    $"run_id={RunId}",
                    $"completed_at={completedAt:O}",
                    $"set_path=/srv/georaeplan/backups/automatic/sets/backup_{RunId}.complete",
                    $"manifest_sha256={SourceManifest}",
                    string.Empty),
                Utf8NoBom);
            WriteReplicaStatus(verifiedAt: DateTimeOffset.UtcNow.AddHours(-1));
        }

        internal string Root { get; }
        internal string StateRoot { get; }

        internal void WriteReplicaStatus(
            string sourceRunId = RunId,
            string sourceManifestSha256 = SourceManifest,
            DateTimeOffset? verifiedAt = null,
            string? extraLine = null)
        {
            var lines = new List<string>
            {
                "replica=ok",
                "replica_id=0123456789abcdef0123456789abcdef",
                $"source_run_id={sourceRunId}",
                $"source_manifest_sha256={sourceManifestSha256}",
                $"replica_set_path=/mnt/georaeplan-replica/sets/replica_{sourceRunId}.complete",
                $"replica_manifest_sha256={new string('c', 64)}",
                $"verified_at={verifiedAt ?? DateTimeOffset.UtcNow.AddHours(-1):O}",
                "restore_catalog_validation=ok",
                "archive_validation=ok"
            };
            if (extraLine is not null)
                lines.Add(extraLine);
            File.WriteAllText(
                Path.Combine(StateRoot, "external-replica-status.txt"),
                string.Join('\n', lines) + "\n",
                Utf8NoBom);
            var failurePath = Path.Combine(
                StateRoot,
                "external-replica-failure-status.txt");
            if (File.Exists(failurePath))
                File.Delete(failurePath);
        }

        internal void WriteFailureStatus(DateTimeOffset failedAt)
        {
            File.WriteAllText(
                Path.Combine(StateRoot, "external-replica-failure-status.txt"),
                string.Join(
                    '\n',
                    "replica=failed",
                    "replica_id=0123456789abcdef0123456789abcdef",
                    $"failed_at={failedAt:O}",
                    "exit_code=3",
                    string.Empty),
                Utf8NoBom);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
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
