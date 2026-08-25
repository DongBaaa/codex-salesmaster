using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BackupRestoreDrillStatusValidationTests
{
    [Fact]
    public async Task Validator_AcceptsOnlyFreshCurrentReplicaBoundNetworklessDrill()
    {
        using var fixture = new RestoreDrillStatusFixture();

        var result = await RunValidatorAsync(fixture.StateRoot);

        Assert.True(
            result.Status == "PASS",
            $"status={result.Status}; reason={result.Reason}; detail={result.Detail}");
        Assert.Equal("restore_drill_verified", result.Reason);
        Assert.Equal(RestoreDrillStatusFixture.RunId, result.SourceRunId);
        Assert.Equal(RestoreDrillStatusFixture.SourceManifest, result.SourceManifestSha256);
        Assert.Equal(RestoreDrillStatusFixture.ReplicaManifest, result.ReplicaManifestSha256);
    }

    [Fact]
    public async Task Validator_RejectsWrongRunExtraFieldsAndNonNetworklessDrill()
    {
        using var fixture = new RestoreDrillStatusFixture();
        fixture.WriteDrill(sourceRunId: "20260812T041837Z-62162");
        var wrongRun = await RunValidatorAsync(fixture.StateRoot);
        Assert.Equal("restore_drill_source_mismatch", wrongRun.Reason);

        fixture.WriteDrill(extraLine: "forged=ok");
        var extra = await RunValidatorAsync(fixture.StateRoot);
        Assert.Equal("restore_drill_status_invalid", extra.Reason);

        fixture.WriteDrill(networkMode: "bridge");
        var network = await RunValidatorAsync(fixture.StateRoot);
        Assert.Equal("restore_drill_status_not_ok", network.Reason);

        fixture.WriteDrill(businessCountDigestContract: "calculated_only");
        var unboundCountDigest = await RunValidatorAsync(fixture.StateRoot);
        Assert.Equal("restore_drill_status_not_ok", unboundCountDigest.Reason);
    }

    [Fact]
    public async Task Validator_RejectsDrillOlderThanReplicaOrMaximumAge()
    {
        using var fixture = new RestoreDrillStatusFixture();
        fixture.WriteDrill(completedAt: DateTimeOffset.UtcNow.AddHours(-3));
        var beforeReplica = await RunValidatorAsync(fixture.StateRoot);
        Assert.Equal("restore_drill_status_stale", beforeReplica.Reason);

        fixture.WriteDrill(completedAt: DateTimeOffset.UtcNow.AddHours(-169));
        var stale = await RunValidatorAsync(fixture.StateRoot);
        Assert.Equal("restore_drill_status_stale", stale.Reason);
    }

    [Fact]
    public async Task Validator_RejectsNewerFailureAndMalformedFailureBinding()
    {
        using var fixture = new RestoreDrillStatusFixture();
        fixture.WriteFailure(DateTimeOffset.UtcNow.AddMinutes(-10));
        var newerFailure = await RunValidatorAsync(fixture.StateRoot);
        Assert.Equal("newer_restore_drill_failure", newerFailure.Reason);

        fixture.WriteFailure(
            DateTimeOffset.UtcNow.AddHours(-2),
            sourceRunId: "20260812T041837Z-62162");
        var wrongBinding = await RunValidatorAsync(fixture.StateRoot);
        Assert.Equal("restore_drill_failure_status_invalid", wrongBinding.Reason);
    }

    private static async Task<ValidationResult> RunValidatorAsync(string stateRoot)
    {
        var scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "ops",
            "Test-GeoraePlanBackupRestoreDrillStatus.ps1");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
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
            throw new TimeoutException("Restore drill status validator timed out.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.True(
            process.ExitCode == 0,
            $"validator exit={process.ExitCode}; stdout={stdout}; stderr={stderr}");
        var json = stdout.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        Assert.False(string.IsNullOrWhiteSpace(json), $"validator returned no JSON; stderr={stderr}");
        return Assert.IsType<ValidationResult>(
            JsonSerializer.Deserialize<ValidationResult>(
                json!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
    }

    private static string FindRepositoryRoot()
    {
        foreach (var searchRoot in new[]
                 {
                     Environment.GetEnvironmentVariable("GEORAEPLAN_REPOSITORY_ROOT"),
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 }.Where(path => !string.IsNullOrWhiteSpace(path)))
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
        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }

    private sealed class RestoreDrillStatusFixture : IDisposable
    {
        internal const string RunId = "20260812T041836Z-62161";
        internal static readonly string SourceManifest = new('a', 64);
        internal static readonly string ReplicaManifest = new('b', 64);
        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private readonly string root;

        internal RestoreDrillStatusFixture()
        {
            root = Path.Combine(
                Path.GetTempPath(),
                "georaeplan-restore-drill-status-tests",
                Guid.NewGuid().ToString("N"));
            StateRoot = Path.Combine(root, "ops", "state");
            Directory.CreateDirectory(StateRoot);
            WriteBackup();
            WriteReplica();
            WriteDrill();
        }

        internal string StateRoot { get; }

        internal void WriteDrill(
            string sourceRunId = RunId,
            string networkMode = "none",
            string businessCountDigestContract = "source_metadata_match",
            DateTimeOffset? completedAt = null,
            string? extraLine = null)
        {
            var lines = new List<string>
            {
                "restore_drill=ok",
                "replica_id=0123456789abcdef0123456789abcdef",
                $"source_run_id={sourceRunId}",
                $"source_manifest_sha256={SourceManifest}",
                $"replica_manifest_sha256={ReplicaManifest}",
                $"image_id=sha256:{new string('c', 64)}",
                $"central_schema_sha256={new string('d', 64)}",
                $"business_schema_sha256={new string('e', 64)}",
                $"business_count_digest_contract={businessCountDigestContract}",
                $"network_mode={networkMode}",
                $"completed_at={(completedAt ?? DateTimeOffset.UtcNow.AddHours(-1)).ToString("o", CultureInfo.InvariantCulture)}"
            };
            if (extraLine is not null) lines.Add(extraLine);
            Write("backup-restore-drill-status.txt", lines);
            var failure = Path.Combine(StateRoot, "backup-restore-drill-failure-status.txt");
            if (File.Exists(failure)) File.Delete(failure);
        }

        internal void WriteFailure(DateTimeOffset failedAt, string sourceRunId = RunId)
            => Write(
                "backup-restore-drill-failure-status.txt",
                [
                    "restore_drill=failed",
                    "replica_id=0123456789abcdef0123456789abcdef",
                    $"source_run_id={sourceRunId}",
                    $"source_manifest_sha256={SourceManifest}",
                    $"replica_manifest_sha256={ReplicaManifest}",
                    $"failed_at={failedAt.ToString("o", CultureInfo.InvariantCulture)}",
                    "reason=restore_failed"
                ]);

        private void WriteBackup()
            => Write(
                "backup-status.txt",
                [
                    "backup=ok",
                    "replica=disabled",
                    $"run_id={RunId}",
                    $"completed_at={DateTimeOffset.UtcNow.AddHours(-2).ToString("o", CultureInfo.InvariantCulture)}",
                    "set_path=/srv/georaeplan/backups/automatic/sets/current",
                    $"manifest_sha256={SourceManifest}"
                ]);

        private void WriteReplica()
            => Write(
                "external-replica-status.txt",
                [
                    "replica=ok",
                    "replica_id=0123456789abcdef0123456789abcdef",
                    $"source_run_id={RunId}",
                    $"source_manifest_sha256={SourceManifest}",
                    $"replica_set_path=/mnt/georaeplan-backup-replica/sets/replica_{RunId}.complete",
                    $"replica_manifest_sha256={ReplicaManifest}",
                    $"verified_at={DateTimeOffset.UtcNow.AddMinutes(-90).ToString("o", CultureInfo.InvariantCulture)}",
                    "restore_catalog_validation=ok",
                    "archive_validation=ok"
                ]);

        private void Write(string name, IEnumerable<string> lines)
            => File.WriteAllText(
                Path.Combine(StateRoot, name),
                string.Join('\n', lines) + "\n",
                Utf8NoBom);

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ValidationResult
    {
        public string Status { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Detail { get; set; } = "";
        public string SourceRunId { get; set; } = "";
        public string SourceManifestSha256 { get; set; } = "";
        public string ReplicaManifestSha256 { get; set; } = "";
    }
}
