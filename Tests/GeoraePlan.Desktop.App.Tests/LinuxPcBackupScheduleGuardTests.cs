using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LinuxPcBackupScheduleGuardTests
{
    [Fact]
    public void RestoreDrill_UsesOnlyAReplicaBoundNetworklessEphemeralDatabaseAndStrictStatusGate()
    {
        var drill = ReadLinuxTool(
            "assets",
            "georaeplan-backup-restore-drill",
            "georaeplan-backup-restore-drill.sh");
        var installer = ReadRepositoryFile(
            "tools",
            "linux",
            "Install-GeoraeplanLinuxPcBackupRestoreDrill.ps1");
        var validator = ReadRepositoryFile(
            "tools",
            "ops",
            "Test-GeoraePlanBackupRestoreDrillStatus.ps1");
        var operationalGate = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");

        foreach (var required in new[]
                 {
                     "restore_drill=ok",
                     "source_run_id",
                     "source_manifest_sha256",
                     "replica_manifest_sha256",
                     "$DOCKER_BIN\" create",
                     "--network none",
                     "--read-only",
                     "--mount \"type=bind,src=$restore_workdir,dst=/var/lib/postgresql/data\"",
                     "cleanup_restore_workdir",
                     "pg_restore --exit-on-error --no-owner --no-privileges",
                     "Users",
                     "Items",
                     "Transactions",
                     "RentalAssets",
                     "Invoices",
                     "Payments",
                     "$DOCKER_BIN\" rm -f"
                 })
        {
            Assert.Contains(required, drill, StringComparison.Ordinal);
        }

        foreach (var forbidden in new[]
                 {
                     "docker compose down",
                     "docker compose restart",
                     "docker system prune",
                     "systemctl restart",
                     "georaeplan-postgres"
                 })
        {
            Assert.DoesNotContain(forbidden, drill, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("if (-not $Apply)", installer, StringComparison.Ordinal);
        Assert.Contains("backup_restore_drill_remote_mutation=none", installer, StringComparison.Ordinal);
        Assert.Contains("current_image_id=", installer, StringComparison.Ordinal);
        Assert.Contains("docker image inspect '$imageId'", installer, StringComparison.Ordinal);
        Assert.Contains("Test-GeoraePlanBackupRestoreDrillStatus.ps1", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$restoreDrillIntegrityPassed", operationalGate, StringComparison.Ordinal);
        Assert.Contains("restore_drill_verified", validator, StringComparison.Ordinal);
        Assert.Contains("replica_manifest_sha256", validator, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerPromptedSudoKeepsThePasswordOffArgumentsEnvironmentAndFiles()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Install-GeoraeplanLinuxPcBackupSchedule.ps1");

        Assert.Contains("[switch]$PromptForSudoCredential", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-SshSudoCommand", source, StringComparison.Ordinal);
        Assert.Contains("Get-Credential `", source, StringComparison.Ordinal);
        Assert.Contains("SecureStringToBSTR", source, StringComparison.Ordinal);
        Assert.Contains("ZeroFreeBSTR", source, StringComparison.Ordinal);
        Assert.Contains("sudo -S -k -p '' sh -s", source, StringComparison.Ordinal);
        Assert.Contains("$OutputEncoding = New-Object Text.UTF8Encoding($false)", source, StringComparison.Ordinal);
        Assert.Contains("$OutputEncoding = $previousOutputEncoding", source, StringComparison.Ordinal);
        Assert.Contains("$stdinPayload =", source, StringComparison.Ordinal);
        Assert.Contains("`n# georaeplan-sudo-command-end", source, StringComparison.Ordinal);
        Assert.Contains("$stdinPayload |", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@($plainPassword, $normalizedCommand) |", source, StringComparison.Ordinal);
        Assert.Contains("backup_schedule_remote_assets=ok", source, StringComparison.Ordinal);
        Assert.Contains("installed_unreadable=%n mode=%a uid=%u gid=%g", source, StringComparison.Ordinal);
        Assert.Contains("$applyCommand.Replace('sudo -n ', '')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$env:SUDO", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Password $plainPassword", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-Content", source[source.IndexOf("function Invoke-SshSudoCommand", StringComparison.Ordinal)..source.IndexOf("if ($LinuxSshHost", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void BackupJob_RequiresOneUnchangedClusterSnapshotWindowAcrossAllLogicalDumps()
    {
        var source = ReadLinuxTool(
            "assets",
            "georaeplan-backup",
            "georaeplan-backup.sh");

        Assert.Contains("pg_current_snapshot()", source, StringComparison.Ordinal);
        Assert.Contains(
            "backup_prerequisite_failed reason=database_snapshot_drift",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "backup_database_snapshot_consistency=ok",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "database_snapshot_consistency=unchanged_across_all_dumps",
            source,
            StringComparison.Ordinal);

        var snapshotBefore = source.IndexOf(
            "database_snapshot_before=",
            StringComparison.Ordinal);
        var dumpLoop = snapshotBefore >= 0
            ? source.IndexOf(
                "for database_name in \"${databases[@]}\"; do",
                snapshotBefore,
                StringComparison.Ordinal)
            : -1;
        var databaseDump = dumpLoop >= 0
            ? source.IndexOf(
                "backup_database_start database=$database_name",
                dumpLoop,
                StringComparison.Ordinal)
            : -1;
        var snapshotAfter = source.IndexOf(
            "database_snapshot_after=",
            StringComparison.Ordinal);
        var filesArchive = source.IndexOf(
            "-czf \"$files_archive\"",
            StringComparison.Ordinal);
        var finalMove = source.IndexOf(
            "mv -T -- \"$staging_dir\" \"$final_dir\"",
            StringComparison.Ordinal);

        Assert.True(
            snapshotBefore >= 0 &&
            snapshotBefore < dumpLoop &&
            dumpLoop < databaseDump &&
            databaseDump < snapshotAfter &&
            snapshotAfter < filesArchive &&
            filesArchive < finalMove,
            "Every discovered logical dump must be rejected unless one unchanged cluster snapshot window encloses the complete dump loop before publication.");
    }

    [Fact]
    public void BackupJob_UsesOnlyThePostgresComposeServiceAndNeverRestartsServices()
    {
        var source = ReadLinuxTool(
            "assets",
            "georaeplan-backup",
            "georaeplan-backup.sh");

        Assert.Contains("--services postgres", source, StringComparison.Ordinal);
        Assert.Contains("exec -T postgres", source, StringComparison.Ordinal);
        Assert.Contains("pg_dump", source, StringComparison.Ordinal);
        Assert.Contains("pg_restore -l", source, StringComparison.Ordinal);
        Assert.True(
            System.Text.RegularExpressions.Regex.Matches(
                source,
                "exec -T api",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count == 3,
            "The backup must inspect the API storage inode, database identities, and process identity before and during capture.");
        Assert.Contains("--services api", source, StringComparison.Ordinal);
        Assert.DoesNotContain("docker compose up", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker compose restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackupJob_PublishesOnlyVerifiedAtomicCompleteSetsAndPreservesReplicaConflict()
    {
        var source = ReadLinuxTool(
            "assets",
            "georaeplan-backup",
            "georaeplan-backup.sh");

        Assert.Contains("flock -n 9", source, StringComparison.Ordinal);
        Assert.Contains("tar -tzf", source, StringComparison.Ordinal);
        Assert.Contains("sha256sum -c", source, StringComparison.Ordinal);
        Assert.Contains("mv -T -- \"$staging_dir\" \"$final_dir\"", source, StringComparison.Ordinal);
        Assert.Contains("GEORAEPLAN_BACKUP_RETENTION_DAYS:-14", source, StringComparison.Ordinal);
        Assert.Contains("backup=ok", source, StringComparison.Ordinal);
        Assert.Contains("backup=failed", source, StringComparison.Ordinal);
        Assert.Contains("replica=disabled", source, StringComparison.Ordinal);
        Assert.DoesNotContain("replica=ok", source, StringComparison.Ordinal);
        Assert.Contains("realpath -m", source, StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_BACKUP_MIN_FREE_BYTES:-2147483648",
            source,
            StringComparison.Ordinal);
        Assert.Contains("backup_capacity_ok", source, StringComparison.Ordinal);
        Assert.Contains("ITWORLD_POSTGRES_DB", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__Default", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__ITWORLD", source, StringComparison.Ordinal);
        Assert.Contains(
            "backup_prerequisite_failed reason=api_database_identity_drift",
            source,
            StringComparison.Ordinal);
        Assert.Contains("backup_api_database_identity=ok", source, StringComparison.Ordinal);
        Assert.Contains("port api 8080", source, StringComparison.Ordinal);
        Assert.Contains("/readyz", source, StringComparison.Ordinal);
        Assert.Contains("\"fileDeletionLeaseProtocol\"", source, StringComparison.Ordinal);
        Assert.Contains("\"shared-flock-v1\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "backup_prerequisite_failed reason=api_not_ready",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "backup_prerequisite_failed reason=api_process_changed_before_capture",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "backup_prerequisite_failed reason=api_process_changed_during_capture",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "backup_prerequisite_failed reason=api_file_deletion_lease_protocol_mismatch",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "backup_api_file_deletion_lease_protocol=shared-flock-v1",
            source,
            StringComparison.Ordinal);
        Assert.Contains("config --environment", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GEORAEPLAN_BUSINESS_DATABASE",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("docker compose pause", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker compose unpause", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".georaeplan-backup-delete.lock", source, StringComparison.Ordinal);
        Assert.Contains("flock -w \"$DELETE_LOCK_TIMEOUT_SECONDS\" 8", source, StringComparison.Ordinal);
        Assert.Contains("flock -u 8", source, StringComparison.Ordinal);
        Assert.Contains("exec 8< \"$FILE_DELETION_LOCK\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("exec 8>> \"$FILE_DELETION_LOCK\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("touch -- \"$FILE_DELETION_LOCK\"", source, StringComparison.Ordinal);
        Assert.Contains("--exclude='*/.*.tmp'", source, StringComparison.Ordinal);
        Assert.Contains(
            "file_deletion_lock_bind_identity_mismatch",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "stat -Lc '%d:%i' \"$FILE_DELETION_LOCK\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "stat -Lc '%d:%i' \"/storage/files/$(basename \"$FILE_DELETION_LOCK\")\"",
            source,
            StringComparison.Ordinal);

        var finalMove = source.IndexOf(
            "mv -T -- \"$staging_dir\" \"$final_dir\"",
            StringComparison.Ordinal);
        var successStatus = source.LastIndexOf(
            "\"$SUCCESS_STATUS\"",
            StringComparison.Ordinal);
        Assert.True(
            finalMove >= 0 && successStatus > finalMove,
            "The success status must only be updated after the complete set is atomically published.");

        var retentionRemoval = source.IndexOf(
            "backup_retention_remove",
            StringComparison.Ordinal);
        Assert.True(
            retentionRemoval > successStatus,
            "Expired sets must not be removed before the new success status safely points at the published set.");

        var deletionLease = source.IndexOf(
            "backup_file_deletion_lease=exclusive",
            StringComparison.Ordinal);
        var databaseDump = source.IndexOf(
            "pg_dump --no-password",
            StringComparison.Ordinal);
        var filesArchive = source.IndexOf(
            "-czf \"$files_archive\"",
            StringComparison.Ordinal);
        var deletionLeaseRelease = source.IndexOf(
            "flock -u 8",
            StringComparison.Ordinal);
        var stableBeforeCapture = source.IndexOf(
            "backup_api_runtime_stable=before_capture",
            StringComparison.Ordinal);
        var stableAfterCapture = source.IndexOf(
            "backup_api_runtime_stable=after_capture",
            StringComparison.Ordinal);
        Assert.True(
            deletionLease >= 0 &&
            deletionLease < stableBeforeCapture &&
            stableBeforeCapture < databaseDump &&
            deletionLease < databaseDump &&
            databaseDump < filesArchive &&
            filesArchive < stableAfterCapture &&
            stableAfterCapture < deletionLeaseRelease &&
            filesArchive < deletionLeaseRelease,
            "The exclusive deletion lease and a stable ready API process must cover every database dump and file archive.");

        var deletionLeaseSource = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Services",
            "StoredFileDeletionLease.cs");
        var reconcilerSource = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Services",
            "StoredFileReferenceReconciler.cs");
        Assert.Contains(
            "internal const string LockFileName = \".georaeplan-backup-delete.lock\"",
            deletionLeaseSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal const string ProtocolVersion = \"shared-flock-v1\"",
            deletionLeaseSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "LockShared | LockNonBlocking",
            deletionLeaseSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (IOException ex) when (IsWouldBlock(ex.HResult))",
            deletionLeaseSource,
            StringComparison.Ordinal);
        Assert.Contains("FileMode.Open", deletionLeaseSource, StringComparison.Ordinal);
        Assert.Contains("FileAccess.Read", deletionLeaseSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.OpenOrCreate", deletionLeaseSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FileAccess.ReadWrite", deletionLeaseSource, StringComparison.Ordinal);
        Assert.Contains(
            "StoredFileDeletionLease.TryAcquireShared(fileStorage.RootPath)",
            reconcilerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (deletionLease is null)",
            reconcilerSource,
            StringComparison.Ordinal);

        var programSource = ReadRepositoryFile(
            "Server",
            "거래플랜.Server.Api",
            "Program.cs");
        Assert.Contains(
            "fileDeletionLeaseProtocol = StoredFileDeletionLease.ProtocolVersion",
            programSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SystemdAssets_RunAOneShotDailyTimerWithoutRestartPolicy()
    {
        var service = ReadLinuxTool(
            "assets",
            "georaeplan-backup",
            "georaeplan-backup.service");
        var timer = ReadLinuxTool(
            "assets",
            "georaeplan-backup",
            "georaeplan-backup.timer");

        Assert.Contains("Type=oneshot", service, StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=/usr/local/sbin/georaeplan-backup.sh",
            service,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Restart=", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Requires=docker.service",
            service,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Wants=docker.service",
            service,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ConditionPathExists=",
            service,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OnCalendar=", timer, StringComparison.Ordinal);
        Assert.Contains("Persistent=true", timer, StringComparison.Ordinal);
        Assert.Contains("RandomizedDelaySec=", timer, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_DefaultsToReadOnlyPlanAndKeepsMutationsBehindApply()
    {
        var installer = ReadLinuxTool(
            "Install-GeoraeplanLinuxPcBackupSchedule.ps1");

        var applyBoundary = installer.IndexOf(
            "if (-not $Apply)",
            StringComparison.Ordinal);
        var remoteMutation = installer.IndexOf(
            "Invoke-SshCommand -SshExe $sshExe -Command \"install -d -m 0700 $quotedStaging\"",
            StringComparison.Ordinal);

        Assert.True(applyBoundary >= 0);
        Assert.True(
            remoteMutation > applyBoundary,
            "Remote mutation must remain behind the explicit -Apply plan boundary.");
        Assert.Contains(
            "backup_schedule_remote_mutation=none",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "config --services | grep -qx postgres",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "config --services | grep -qx api",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "storage_bind_identity=ok",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "storage/files/.georaeplan-backup-delete.lock",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "sudo -n test -L \"`$lock\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "sudo -n touch \"`$lock\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "sudo -n chmod 0644 \"`$lock\"",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "chmod 0666",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "database_identity=ok",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConnectionStrings__Default",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConnectionStrings__ITWORLD",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "api_database_identity_drift",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "api_database_identity=ok",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "port api 8080",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "api_health_endpoint_not_loopback",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "/readyz",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "api_ready=ok",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"fileDeletionLeaseProtocol\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"shared-flock-v1\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "api_file_deletion_lease_protocol_mismatch",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "file_deletion_lease_protocol=shared-flock-v1",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "$LinuxRemoteRoot -match '(^|/)\\.{1,2}(/|$)'",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]::Equals($LinuxRemoteRoot, '/srv/georaeplan'",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "installed backup script and systemd unit use that fixed production root",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "systemctl restart",
            installer,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplicaContract_RequiresStrictExternalStatusWhileSourceBackupRemainsReplicaDisabled()
    {
        var backup = ReadLinuxTool(
            "assets",
            "georaeplan-backup",
            "georaeplan-backup.sh");
        var readme = ReadLinuxTool(
            "assets",
            "georaeplan-backup",
            "README.md");
        var environmentExample = ReadRepositoryFile(
            "infra",
            "linux",
            ".env.example");
        var operationalGate = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");
        var replicaValidator = ReadRepositoryFile(
            "tools",
            "ops",
            "Test-GeoraePlanExternalReplicaStatus.ps1");
        var replica = ReadLinuxTool(
            "assets",
            "georaeplan-backup-replica",
            "georaeplan-backup-replica.sh");
        var replicaReadme = ReadLinuxTool(
            "assets",
            "georaeplan-backup-replica",
            "README.md");

        Assert.Contains(
            "EXTERNAL_REPLICA_ENABLED=false",
            environmentExample,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-GeoraePlanExternalReplicaStatus.ps1",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Contains("$replicaIntegrityPassed", operationalGate, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$replica -match 'replica=ok'",
            operationalGate,
            StringComparison.Ordinal);
        Assert.Contains("replica=disabled", backup, StringComparison.Ordinal);
        Assert.DoesNotContain("replica=ok", backup, StringComparison.Ordinal);
        Assert.Contains("replica=ok", replica, StringComparison.Ordinal);
        Assert.Contains("source_manifest_sha256", replicaValidator, StringComparison.Ordinal);
        Assert.Contains("replica_manifest_sha256", replicaValidator, StringComparison.Ordinal);
        Assert.Contains("restore_drill=not_proven", replicaReadme, StringComparison.Ordinal);
        Assert.Contains("replica=disabled", readme, StringComparison.Ordinal);
    }

    private static string ReadLinuxTool(params string[] relativeParts)
        => ReadRepositoryFile(
            ["tools", "linux", .. relativeParts]);

    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        var pathParts = new[] { FindRepositoryRoot() }
            .Concat(relativeParts)
            .ToArray();
        return File.ReadAllText(Path.Combine(pathParts));
    }

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

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }
}
