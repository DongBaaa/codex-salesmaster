using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedUserBootstrapSafetyTests
{
    [Fact]
    public async Task SourceUsersSnapshotAcl_AcceptsRestrictedRootAndRejectsUnsafeBoundaries()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Assert-SourceUsersSnapshotAcl"],
            """
            $allowedRoot = Join-Path $PSScriptRoot 'protected-snapshot-root'
            New-Item -ItemType Directory -Path $allowedRoot -Force |
                Out-Null

            $currentSid =
                [Security.Principal.WindowsIdentity]::GetCurrent().User
            $currentRule =
                [Security.AccessControl.FileSystemAccessRule]::new(
                    $currentSid,
                    [Security.AccessControl.FileSystemRights]::FullControl,
                    (
                        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                        [Security.AccessControl.InheritanceFlags]::ObjectInherit
                    ),
                    [Security.AccessControl.PropagationFlags]::None,
                    [Security.AccessControl.AccessControlType]::Allow)
            $restrictedAcl =
                [Security.AccessControl.DirectorySecurity]::new()
            $restrictedAcl.SetOwner($currentSid)
            $restrictedAcl.SetAccessRuleProtection($true, $false)
            [void]$restrictedAcl.AddAccessRule($currentRule)
            Set-Acl -LiteralPath $allowedRoot -AclObject $restrictedAcl

            $snapshotPath = Join-Path $allowedRoot 'source-users.json'
            [IO.File]::WriteAllText($snapshotPath, '{}')
            Assert-SourceUsersSnapshotAcl `
                -Path $snapshotPath `
                -AllowedRoot $allowedRoot

            $restrictedRootAcl =
                Microsoft.PowerShell.Security\Get-Acl `
                    -LiteralPath $allowedRoot
            $restrictedFileAcl =
                Microsoft.PowerShell.Security\Get-Acl `
                    -LiteralPath $snapshotPath
            $unprotectedAcl =
                [Security.AccessControl.DirectorySecurity]::new()
            $unprotectedAcl.SetSecurityDescriptorBinaryForm(
                $restrictedRootAcl.GetSecurityDescriptorBinaryForm())
            $unprotectedAcl.SetAccessRuleProtection($false, $false)
            $script:aclProbeMode = 'unprotected'
            function Get-Acl {
                param([string]$LiteralPath)
                if (
                    [string]::Equals(
                        $LiteralPath,
                        $allowedRoot,
                        [StringComparison]::OrdinalIgnoreCase)
                ) {
                    if ($script:aclProbeMode -eq 'unprotected') {
                        return $unprotectedAcl
                    }
                    return $unsafeAcl
                }
                return $restrictedFileAcl
            }
            $unprotectedRejected = $false
            try {
                Assert-SourceUsersSnapshotAcl `
                    -Path $snapshotPath `
                    -AllowedRoot $allowedRoot
            }
            catch {
                if (
                    [string]$_.Exception.Message -notmatch
                        'allowed root ACL is not protected'
                ) {
                    throw
                }
                $unprotectedRejected = $true
            }
            if (-not $unprotectedRejected) {
                throw 'An inheritance-enabled snapshot root was accepted.'
            }

            $unsupportedSid =
                [Security.Principal.SecurityIdentifier]::new(
                    'S-1-5-32-545')
            $unsupportedRule =
                [Security.AccessControl.FileSystemAccessRule]::new(
                    $unsupportedSid,
                    [Security.AccessControl.FileSystemRights]::ReadAndExecute,
                    (
                        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                        [Security.AccessControl.InheritanceFlags]::ObjectInherit
                    ),
                    [Security.AccessControl.PropagationFlags]::None,
                    [Security.AccessControl.AccessControlType]::Allow)
            $unsafeAcl =
                [Security.AccessControl.DirectorySecurity]::new()
            $unsafeAcl.SetSecurityDescriptorBinaryForm(
                $restrictedRootAcl.GetSecurityDescriptorBinaryForm())
            [void]$unsafeAcl.AddAccessRule($unsupportedRule)
            $script:aclProbeMode = 'unsupported'

            $unsupportedRejected = $false
            try {
                Assert-SourceUsersSnapshotAcl `
                    -Path $snapshotPath `
                    -AllowedRoot $allowedRoot
            }
            catch {
                if (
                    [string]$_.Exception.Message -notmatch
                        'unsupported identity'
                ) {
                    throw
                }
                $unsupportedRejected = $true
            }
            if (-not $unsupportedRejected) {
                throw 'A broadly readable snapshot root was accepted.'
            }

            Write-Output 'source-users-snapshot-acl-boundary-verified'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "source-users-snapshot-acl-boundary-verified",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SyncDiagSeedRetry_NeverDeletesOutboxForMissingLocalRows()
    {
        var programSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "Program.cs"));
        var reconcilerSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "IsolatedSeedRetryOutboxReconciler.cs"));

        Assert.Contains(
            "RemoveCleanOutboxAsync<LocalRentalManagementCompany>(db)",
            programSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "static async Task<int> RemoveCleanOutboxAsync<TEntity>(",
            programSource,
            StringComparison.Ordinal);
        var rentalAssetReconcileIndex = programSource.IndexOf(
            "IsolatedSeedRetryRentalAssetReconciler.ReconcileAsync(",
            StringComparison.Ordinal);
        var cleanOutboxIndex = programSource.IndexOf(
            "await RemoveCleanOutboxAsync<LocalRentalManagementCompany>(db)",
            rentalAssetReconcileIndex >= 0 ? rentalAssetReconcileIndex : 0,
            StringComparison.Ordinal);
        Assert.True(
            rentalAssetReconcileIndex >= 0 &&
            cleanOutboxIndex > rentalAssetReconcileIndex,
            "The exact rental asset repair is not confined to retry preparation before clean outbox reconciliation.");
        Assert.Contains(
            "unlinked_excluded_rental_assets=",
            programSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "closed_rental_assignment_histories=",
            programSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "removed_collateral_failed_assignment_outbox=",
            programSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoveExactFailedOutboxForDirtyEntitiesAsync<LocalRentalAssetAssignmentHistory>",
            programSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "removed_superseded_sent_assignment_outbox=",
            programSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SupersedeUniqueSentOutboxForDirtyEntitiesAsync<LocalRentalAssetAssignmentHistory>",
            programSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "BuildExpectedMutationId",
            reconcilerSource,
            StringComparison.Ordinal);
        var existingEntityFilterIndex = reconcilerSource.IndexOf(
            "pendingEntityIds.Contains(entity.Id) &&",
            StringComparison.Ordinal);
        var cleanEntityFilterIndex = reconcilerSource.IndexOf(
            "!entity.IsDirty",
            existingEntityFilterIndex,
            StringComparison.Ordinal);
        Assert.True(
            existingEntityFilterIndex >= 0 &&
            cleanEntityFilterIndex > existingEntityFilterIndex &&
            cleanEntityFilterIndex - existingEntityFilterIndex < 100);
        Assert.DoesNotContain(
            "allowMissing",
            programSource + reconcilerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncDiagSeedRetry_RemovesOnlyPresentCleanEntityOutbox()
    {
        var databasePath = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"seed-retry-outbox-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var cleanId = Guid.NewGuid();
            var dirtyId = Guid.NewGuid();
            var missingId = Guid.NewGuid();
            db.RentalManagementCompanies.AddRange(
                new LocalRentalManagementCompany
                {
                    Id = cleanId,
                    Code = "CLEAN",
                    Name = "Clean company",
                    IsDirty = false
                },
                new LocalRentalManagementCompany
                {
                    Id = dirtyId,
                    Code = "DIRTY",
                    Name = "Dirty company",
                    IsDirty = true
                });
            db.SyncOutboxEntries.AddRange(
                CreateFailedOutbox(cleanId),
                CreateFailedOutbox(dirtyId),
                CreateFailedOutbox(missingId));
            await db.SaveChangesAsync();

            var removed =
                await IsolatedSeedRetryOutboxReconciler
                    .RemoveCleanOutboxAsync<LocalRentalManagementCompany>(db);

            Assert.Equal(1, removed);
            db.ChangeTracker.Clear();
            var remainingIds = await db.SyncOutboxEntries
                .AsNoTracking()
                .Select(entry => entry.EntityId)
                .OrderBy(id => id)
                .ToListAsync();
            Assert.Equal(
                new[] { dirtyId, missingId }.OrderBy(id => id),
                remainingIds);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }

        static LocalSyncOutboxEntry CreateFailedOutbox(Guid entityId)
            => new()
            {
                EntityName = nameof(LocalRentalManagementCompany),
                EntityId = entityId,
                MutationId = $"fixture:{entityId:N}",
                Status = "Failed"
            };
    }

    [Fact]
    public async Task SyncDiagSeedRetry_RemovesOnlyExactSingleFailedOutboxForStillDirtyAssignmentHistory()
    {
        var databasePath = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"seed-retry-failed-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var exact = NewHistory(Guid.NewGuid(), dirty: true, revision: 7);
            var wrongMutation = NewHistory(Guid.NewGuid(), dirty: true, revision: 8);
            var duplicate = NewHistory(Guid.NewGuid(), dirty: true, revision: 9);
            var clean = NewHistory(Guid.NewGuid(), dirty: false, revision: 10);
            var prepared = NewHistory(Guid.NewGuid(), dirty: true, revision: 11);
            var wrongMutationOutbox = NewOutbox(wrongMutation, "Failed");
            wrongMutationOutbox.MutationId = "not-the-exact-mutation";
            var duplicateOutboxA = NewOutbox(duplicate, "Failed");
            duplicateOutboxA.MutationId += ":duplicate-a";
            var duplicateOutboxB = NewOutbox(duplicate, "Failed");
            duplicateOutboxB.MutationId += ":duplicate-b";
            db.RentalAssetAssignmentHistories.AddRange(
                exact,
                wrongMutation,
                duplicate,
                clean,
                prepared);
            db.SyncOutboxEntries.AddRange(
                NewOutbox(exact, "Failed"),
                wrongMutationOutbox,
                duplicateOutboxA,
                duplicateOutboxB,
                NewOutbox(clean, "Failed"),
                NewOutbox(prepared, "Prepared"),
                new LocalSyncOutboxEntry
                {
                    EntityName = nameof(LocalRentalAssetAssignmentHistory),
                    EntityId = Guid.NewGuid(),
                    MutationId = "missing",
                    Status = "Failed"
                });
            await db.SaveChangesAsync();

            var removed = await IsolatedSeedRetryOutboxReconciler
                .RemoveExactFailedOutboxForDirtyEntitiesAsync<
                    LocalRentalAssetAssignmentHistory>(db);

            Assert.Equal(1, removed);
            db.ChangeTracker.Clear();
            var remaining = await db.SyncOutboxEntries
                .AsNoTracking()
                .ToListAsync();
            Assert.DoesNotContain(
                remaining,
                entry => entry.EntityId == exact.Id);
            Assert.Equal(
                2,
                remaining.Count(entry => entry.EntityId == duplicate.Id));
            Assert.Contains(
                remaining,
                entry => entry.EntityId == wrongMutation.Id);
            Assert.Contains(
                remaining,
                entry => entry.EntityId == clean.Id);
            Assert.Contains(
                remaining,
                entry => entry.EntityId == prepared.Id &&
                         entry.Status == "Prepared");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }

        static LocalRentalAssetAssignmentHistory NewHistory(
            Guid id,
            bool dirty,
            long revision)
            => new()
            {
                Id = id,
                AssetId = Guid.NewGuid(),
                BillingProfileId = Guid.NewGuid(),
                CustomerId = Guid.NewGuid(),
                LinkedAtUtc = new DateTime(
                    2026,
                    8,
                    11,
                    0,
                    0,
                    revision > 59 ? 59 : (int)revision,
                    DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(
                    2026,
                    8,
                    11,
                    1,
                    0,
                    revision > 59 ? 59 : (int)revision,
                    DateTimeKind.Utc),
                Revision = revision,
                IsDirty = dirty,
                TenantCode = "TENANT",
                ResponsibleOfficeCode = "USENET"
            };

        static LocalSyncOutboxEntry NewOutbox(
            LocalRentalAssetAssignmentHistory history,
            string status)
        {
            const string deviceId = "fixture-device";
            var entityName = nameof(LocalRentalAssetAssignmentHistory);
            return new LocalSyncOutboxEntry
            {
                EntityName = entityName,
                EntityId = history.Id,
                DeviceId = deviceId,
                ExpectedRevision = history.Revision,
                MutationId =
                    $"{deviceId}:{entityName}:{history.Id:N}:" +
                    $"{history.Revision}:{history.UpdatedAtUtc.Ticks}:0",
                TenantCode = history.TenantCode,
                OfficeCode = history.ResponsibleOfficeCode,
                ResponsibleOfficeCode = history.ResponsibleOfficeCode,
                BusinessDatabaseName = "fixture-db",
                SessionId = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Status = status,
                ErrorMessage = "fixture conflict"
            };
        }
    }

    [Fact]
    public async Task SyncDiagSeedRetry_SupersedesEveryUniqueSentReceiptForDirtyPayload()
    {
        var databasePath = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"seed-retry-stale-sent-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();

            var stale = NewHistory(Guid.NewGuid(), revision: 5);
            var current = NewHistory(Guid.NewGuid(), revision: 6);
            var duplicate = NewHistory(Guid.NewGuid(), revision: 7);
            var failed = NewHistory(Guid.NewGuid(), revision: 8);
            var staleUpdatedAtUtc = stale.UpdatedAtUtc;
            var currentUpdatedAtUtc = current.UpdatedAtUtc;
            db.RentalAssetAssignmentHistories.AddRange(
                stale,
                current,
                duplicate,
                failed);

            var staleRow = NewOutbox(stale, "Sent");
            staleRow.MutationId = "superseded-mutation";
            var duplicateA = NewOutbox(duplicate, "Sent");
            duplicateA.MutationId += ":a";
            var duplicateB = NewOutbox(duplicate, "Sent");
            duplicateB.MutationId += ":b";
            db.SyncOutboxEntries.AddRange(
                staleRow,
                NewOutbox(current, "Sent"),
                duplicateA,
                duplicateB,
                NewOutbox(failed, "Failed"));
            await db.SaveChangesAsync();

            var supersedeAtUtc = new DateTime(
                2026,
                8,
                11,
                3,
                0,
                0,
                DateTimeKind.Utc);
            var removed = await IsolatedSeedRetryOutboxReconciler
                .SupersedeUniqueSentOutboxForDirtyEntitiesAsync<
                    LocalRentalAssetAssignmentHistory>(db, supersedeAtUtc);

            Assert.Equal(2, removed);
            var remaining = await db.SyncOutboxEntries
                .AsNoTracking()
                .ToListAsync();
            Assert.DoesNotContain(remaining, row => row.EntityId == stale.Id);
            Assert.DoesNotContain(remaining, row => row.EntityId == current.Id);
            Assert.Equal(2, remaining.Count(row => row.EntityId == duplicate.Id));
            Assert.Contains(
                remaining,
                row => row.EntityId == failed.Id && row.Status == "Failed");
            var refreshed = await db.RentalAssetAssignmentHistories
                .AsNoTracking()
                .Where(history => history.Id == stale.Id || history.Id == current.Id)
                .ToDictionaryAsync(history => history.Id);
            Assert.True(refreshed[stale.Id].UpdatedAtUtc > staleUpdatedAtUtc);
            Assert.True(refreshed[current.Id].UpdatedAtUtc > currentUpdatedAtUtc);
            Assert.Equal(supersedeAtUtc, refreshed[stale.Id].UpdatedAtUtc);
            Assert.Equal(supersedeAtUtc, refreshed[current.Id].UpdatedAtUtc);
            Assert.True(refreshed[stale.Id].IsDirty);
            Assert.True(refreshed[current.Id].IsDirty);
            await transaction.CommitAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }

        static LocalRentalAssetAssignmentHistory NewHistory(
            Guid id,
            long revision)
            => new()
            {
                Id = id,
                AssetId = Guid.NewGuid(),
                UpdatedAtUtc = new DateTime(
                    2026,
                    8,
                    11,
                    2,
                    0,
                    (int)revision,
                    DateTimeKind.Utc),
                Revision = revision,
                IsDirty = true,
                TenantCode = "TENANT",
                ResponsibleOfficeCode = "USENET"
            };

        static LocalSyncOutboxEntry NewOutbox(
            LocalRentalAssetAssignmentHistory history,
            string status)
        {
            const string deviceId = "fixture-device";
            var entityName = nameof(LocalRentalAssetAssignmentHistory);
            return new LocalSyncOutboxEntry
            {
                EntityName = entityName,
                EntityId = history.Id,
                DeviceId = deviceId,
                ExpectedRevision = history.Revision,
                MutationId =
                    $"{deviceId}:{entityName}:{history.Id:N}:" +
                    $"{history.Revision}:{history.UpdatedAtUtc.Ticks}:0",
                TenantCode = history.TenantCode,
                OfficeCode = history.ResponsibleOfficeCode,
                ResponsibleOfficeCode = history.ResponsibleOfficeCode,
                BusinessDatabaseName = "fixture-db",
                SessionId = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Status = status
            };
        }
    }

    [Fact]
    public async Task GetStoredSyncCredentials_ValidatesEnvelopeAndDecryptsOnlyInParent()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "ConvertFrom-StoredCredentialEnvelopeProcessResult",
                "Get-StoredSyncCredentialsFromLocalState"
            ],
            """
            Add-Type -AssemblyName System.Security
            $plain1 = [Text.Encoding]::UTF8.GetBytes('first-secret')
            $plain2 = [Text.Encoding]::UTF8.GetBytes('second-secret')
            try {
                $protected1 = [System.Security.Cryptography.ProtectedData]::Protect(
                    $plain1,
                    $null,
                    [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
                $protected2 = [System.Security.Cryptography.ProtectedData]::Protect(
                    $plain2,
                    $null,
                    [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
                $script:envelope = [pscustomobject]@{
                    schemaVersion = 1
                    protection = 'DPAPI-CurrentUser'
                    credentials = @(
                        [pscustomobject]@{
                            OfficeCode = 'USENET'
                            TenantCode = 'USENET_GROUP'
                            Username = 'first-user'
                            PasswordProtected = [Convert]::ToBase64String($protected1)
                            SavedAtUtc = '2026-07-29T00:00:00.0000000Z'
                        },
                        [pscustomobject]@{
                            OfficeCode = 'ITWORLD'
                            TenantCode = 'ITWORLD'
                            Username = 'second-user'
                            PasswordProtected = [Convert]::ToBase64String($protected2)
                            SavedAtUtc = '2026-07-29T00:00:00.0000000Z'
                        }
                    )
                } | ConvertTo-Json -Compress -Depth 10
            }
            finally {
                if ($null -ne $plain1) {
                    [Array]::Clear($plain1, 0, $plain1.Length)
                }
                if ($null -ne $plain2) {
                    [Array]::Clear($plain2, 0, $plain2.Length)
                }
                if ($null -ne $protected1) {
                    [Array]::Clear($protected1, 0, $protected1.Length)
                }
                if ($null -ne $protected2) {
                    [Array]::Clear($protected2, 0, $protected2.Length)
                }
            }

            function Invoke-StoredCredentialEnvelopeProcess {
                param(
                    [string]$DotnetExe,
                    [string]$SyncDiagProject
                )
                return [pscustomobject]@{
                    ExitCode = 0
                    Stdout = $script:envelope + [Environment]::NewLine
                    Stderr = ''
                    InvocationMode = 'fixture'
                }
            }

            function Invoke-WithProcessEnvironment {
                param(
                    [hashtable]$Variables,
                    [scriptblock]$Action
                )
                & $Action
            }

            function Write-Utf8File {
                param(
                    [string]$Path,
                    [string]$Content
                )
                $script:sanitizedLog = $Content
            }

            $credentials = @(
                Get-StoredSyncCredentialsFromLocalState `
                    -DotnetExe 'dotnet.exe' `
                    -SyncDiagProject 'SyncDiag.csproj' `
                    -AppRoot 'D:\isolated-app-data' `
                    -LogPath 'D:\sanitized.log'
            )
            if (
                $credentials.Count -ne 2 -or
                [string]$credentials[0].Username -ne 'first-user' -or
                [string]$credentials[1].Username -ne 'second-user' -or
                [string]$credentials[0].Password -ne 'first-secret' -or
                [string]$credentials[1].Password -ne 'second-secret'
            ) {
                throw (
                    'Stored credential array was collapsed: count=' +
                    $credentials.Count)
            }
            if (
                $script:sanitizedLog -match 'first-secret' -or
                $script:sanitizedLog -match 'second-secret'
            ) {
                throw 'A stored password was written to the sanitized log.'
            }
            if ($script:sanitizedLog -match 'PasswordProtected') {
                throw 'Ciphertext was written to the sanitized log.'
            }
            Write-Output 'credential-envelope-parent-decryption-verified'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "credential-envelope-parent-decryption-verified",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"protection\":\"DPAPI-CurrentUser\",\"credentials\":[]}\nnoise", "", "multiline")]
    [InlineData("noise", "", "invalid-json")]
    [InlineData("{\"schemaVersion\":1,\"protection\":\"DPAPI-CurrentUser\",\"credentials\":[],\"extra\":1}", "", "extra-field")]
    [InlineData("{\"schemaVersion\":1,\"protection\":\"DPAPI-CurrentUser\",\"credentials\":[]}", "child-secret", "stderr")]
    [InlineData("{\"schemaVersion\":1,\"protection\":\"DPAPI-CurrentUser\",\"credentials\":[{\"OfficeCode\":\"USENET\",\"TenantCode\":\"USENET_GROUP\",\"Username\":\"fixture\",\"PasswordProtected\":\"bm90LWRwYXBp\",\"SavedAtUtc\":\"2026-07-29T00:00:00.0000000Z\"}]}", "", "decrypt")]
    public async Task StoredCredentialEnvelope_InvalidChildOutputIsRejectedAndRedacted(
        string stdout,
        string stderr,
        string scenario)
    {
        var result = await RunPreparationFunctionsAsync(
            ["ConvertFrom-StoredCredentialEnvelopeProcessResult"],
            $$"""
            function Write-Utf8File {
                param([string]$Path, [string]$Content)
                $script:log = $Content
            }
            $child = [pscustomobject]@{
                ExitCode = 0
                Stdout = @'
            {{stdout}}
            '@
                Stderr = @'
            {{stderr}}
            '@
                InvocationMode = 'fixture'
            }
            try {
                ConvertFrom-StoredCredentialEnvelopeProcessResult `
                    -Result $child `
                    -LogPath 'D:\sanitized.log' |
                    Out-Null
                throw 'invalid child output was accepted'
            }
            catch {
                $combined = [string]$_.Exception.Message + [string]$script:log
                if (
                    $combined -match 'child-secret' -or
                    $combined -match 'bm90LWRwYXBp'
                ) {
                    throw 'Sensitive child output leaked.'
                }
            }
            Write-Output 'redacted-{{scenario}}'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            $"redacted-{scenario}",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportSourceUsersSnapshot_AcceptsFreshCompleteSnapshot()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "Initialize-TestEnvironmentFinalPathNativeMethods",
                "ConvertTo-NormalizedFullPath",
                "Get-FinalExistingPath",
                "Resolve-PhysicalPathIdentity",
                "Get-SourceUsersSnapshotKnownPermissions",
                "Get-SourceUsersSnapshotTextSha256",
                "Get-SourceUsersSnapshotOrdinalSortKey",
                "ConvertTo-SourceUsersSnapshotCanonicalJsonString",
                "Get-SourceUsersSnapshotCanonicalJson",
                "Get-SourceUsersSnapshotScopeCounts",
                "Import-SourceUsersSnapshot"
            ],
            """
            $allowedRoot = Join-Path `
                ([IO.Path]::GetTempPath()) `
                ('source-users-valid-' + [Guid]::NewGuid().ToString('N'))
            $snapshotPath = Join-Path $allowedRoot 'source-users.json'
            try {
                [IO.Directory]::CreateDirectory($allowedRoot) | Out-Null
                $users = @(
                    [pscustomobject][ordered]@{
                        username = 'snapshot-admin'
                        role = 'Admin'
                        tenantCode = 'USENET_GROUP'
                        officeCode = 'USENET'
                        scopeType = 'Admin'
                        isActive = $true
                        permissions = @(
                            'Settings.Edit',
                            'Rental.ViewAll'
                        )
                    }
                )
                $canonicalJson =
                    Get-SourceUsersSnapshotCanonicalJson -Users $users
                $payload = [ordered]@{
                    schemaVersion = 1
                    sourceKind = 'georaeplan-user-permission-snapshot-v1'
                    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
                    isComplete = $true
                    userCount = 1
                    permissionCount = 2
                    scopeCounts =
                        @(Get-SourceUsersSnapshotScopeCounts -Users $users)
                    canonicalSha256 =
                        Get-SourceUsersSnapshotTextSha256 -Text $canonicalJson
                    users = $users
                }
                [IO.File]::WriteAllText(
                    $snapshotPath,
                    ($payload | ConvertTo-Json -Depth 20),
                    [Text.UTF8Encoding]::new($false))
                $expectedSha256 = (
                    Get-FileHash `
                        -LiteralPath $snapshotPath `
                        -Algorithm SHA256
                ).Hash

                $snapshot = Import-SourceUsersSnapshot `
                    -Path $snapshotPath `
                    -AllowedRoot $allowedRoot `
                    -ExpectedSha256 $expectedSha256
                if (
                    $snapshot.SchemaVersion -ne 1 -or
                    $snapshot.SourceKind -cne
                        'georaeplan-user-permission-snapshot-v1' -or
                    -not $snapshot.IsComplete -or
                    $snapshot.UserCount -ne 1 -or
                    $snapshot.PermissionCount -ne 2 -or
                    @($snapshot.ScopeCounts).Count -ne 1 -or
                    @($snapshot.Users).Count -ne 1 -or
                    [string]$snapshot.Users[0].username -cne
                        'snapshot-admin' -or
                    (@($snapshot.Users[0].permissions) -join ',') -cne
                        'Rental.ViewAll,Settings.Edit' -or
                    [string]$snapshot.SnapshotSha256 -cnotmatch
                        '^[0-9A-F]{64}$'
                ) {
                    throw 'The valid source users snapshot was not normalized.'
                }

                Write-Output 'valid-source-users-snapshot-imported'
            }
            finally {
                Remove-Item `
                    -LiteralPath $allowedRoot `
                    -Recurse `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "valid-source-users-snapshot-imported",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportSourceUsersSnapshot_RejectsPasswordHashWithoutLeakingValue()
    {
        const string secret = "snapshot-password-hash-secret-must-not-leak";
        var result = await RunPreparationFunctionsAsync(
            [
                "Initialize-TestEnvironmentFinalPathNativeMethods",
                "ConvertTo-NormalizedFullPath",
                "Get-FinalExistingPath",
                "Resolve-PhysicalPathIdentity",
                "Get-SourceUsersSnapshotKnownPermissions",
                "Get-SourceUsersSnapshotTextSha256",
                "Get-SourceUsersSnapshotOrdinalSortKey",
                "ConvertTo-SourceUsersSnapshotCanonicalJsonString",
                "Get-SourceUsersSnapshotCanonicalJson",
                "Get-SourceUsersSnapshotScopeCounts",
                "Import-SourceUsersSnapshot"
            ],
            $$"""
            $allowedRoot = Join-Path `
                ([IO.Path]::GetTempPath()) `
                ('source-users-secret-' + [Guid]::NewGuid().ToString('N'))
            $snapshotPath = Join-Path $allowedRoot 'source-users.json'
            try {
                [IO.Directory]::CreateDirectory($allowedRoot) | Out-Null
                $users = @(
                    [pscustomobject][ordered]@{
                        username = 'snapshot-admin'
                        role = 'Admin'
                        tenantCode = 'USENET_GROUP'
                        officeCode = 'USENET'
                        scopeType = 'Admin'
                        isActive = $true
                        permissions = @('Settings.Edit')
                        passwordHash = '{{secret}}'
                    }
                )
                $canonicalJson =
                    Get-SourceUsersSnapshotCanonicalJson -Users $users
                $payload = [ordered]@{
                    schemaVersion = 1
                    sourceKind = 'georaeplan-user-permission-snapshot-v1'
                    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
                    isComplete = $true
                    userCount = 1
                    permissionCount = 1
                    scopeCounts =
                        @(Get-SourceUsersSnapshotScopeCounts -Users $users)
                    canonicalSha256 =
                        Get-SourceUsersSnapshotTextSha256 -Text $canonicalJson
                    users = $users
                }
                [IO.File]::WriteAllText(
                    $snapshotPath,
                    ($payload | ConvertTo-Json -Depth 20),
                    [Text.UTF8Encoding]::new($false))
                $expectedSha256 = (
                    Get-FileHash `
                        -LiteralPath $snapshotPath `
                        -Algorithm SHA256
                ).Hash

                Import-SourceUsersSnapshot `
                    -Path $snapshotPath `
                    -AllowedRoot $allowedRoot `
                    -ExpectedSha256 $expectedSha256 |
                    Out-Null
            }
            finally {
                Remove-Item `
                    -LiteralPath $allowedRoot `
                    -Recurse `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "contains missing or unsupported user fields",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secret,
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportSourceUsersSnapshot_FailsClosedForIncompleteCountsAndMissingSystemAdmin()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "Initialize-TestEnvironmentFinalPathNativeMethods",
                "ConvertTo-NormalizedFullPath",
                "Get-FinalExistingPath",
                "Resolve-PhysicalPathIdentity",
                "Get-SourceUsersSnapshotKnownPermissions",
                "Get-SourceUsersSnapshotTextSha256",
                "Get-SourceUsersSnapshotOrdinalSortKey",
                "ConvertTo-SourceUsersSnapshotCanonicalJsonString",
                "Get-SourceUsersSnapshotCanonicalJson",
                "Get-SourceUsersSnapshotScopeCounts",
                "Import-SourceUsersSnapshot"
            ],
            """
            function New-ValidSourceUsersSnapshotPayload {
                $users = @(
                    [pscustomobject][ordered]@{
                        username = 'snapshot-admin'
                        role = 'Admin'
                        tenantCode = 'USENET_GROUP'
                        officeCode = 'USENET'
                        scopeType = 'Admin'
                        isActive = $true
                        permissions = @(
                            'Settings.Edit',
                            'Rental.ViewAll'
                        )
                    }
                )
                $canonicalJson =
                    Get-SourceUsersSnapshotCanonicalJson -Users $users
                return [ordered]@{
                    schemaVersion = 1
                    sourceKind = 'georaeplan-user-permission-snapshot-v1'
                    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
                    isComplete = $true
                    userCount = 1
                    permissionCount = 2
                    scopeCounts =
                        @(Get-SourceUsersSnapshotScopeCounts -Users $users)
                    canonicalSha256 =
                        Get-SourceUsersSnapshotTextSha256 -Text $canonicalJson
                    users = $users
                }
            }

            $allowedRoot = Join-Path `
                ([IO.Path]::GetTempPath()) `
                ('source-users-invalid-' + [Guid]::NewGuid().ToString('N'))
            $snapshotPath = Join-Path $allowedRoot 'source-users.json'
            try {
                [IO.Directory]::CreateDirectory($allowedRoot) | Out-Null
                $cases = @(
                    [pscustomobject]@{
                        Name = 'incomplete'
                        Configure = {
                            param($snapshot)
                            $snapshot.isComplete = $false
                        }
                        ExpectedError = 'must declare isComplete=true'
                    },
                    [pscustomobject]@{
                        Name = 'count-mismatch'
                        Configure = {
                            param($snapshot)
                            $snapshot.userCount = 2
                        }
                        ExpectedError = 'userCount does not match'
                    },
                    [pscustomobject]@{
                        Name = 'missing-system-admin'
                        Configure = {
                            param($snapshot)
                            $snapshot.users[0].scopeType = 'TenantAll'
                        }
                        ExpectedError = 'has no active Admin/Admin user'
                    },
                    [pscustomobject]@{
                        Name = 'scope-count-mismatch'
                        Configure = {
                            param($snapshot)
                            $snapshot.scopeCounts[0].permissionCount = 1
                        }
                        ExpectedError = 'scopeCounts do not match users'
                    },
                    [pscustomobject]@{
                        Name = 'canonical-digest-mismatch'
                        Configure = {
                            param($snapshot)
                            $snapshot.canonicalSha256 = ('0' * 64)
                        }
                        ExpectedError =
                            'canonicalSha256 does not match users'
                    },
                    [pscustomobject]@{
                        Name = 'unknown-permission'
                        Configure = {
                            param($snapshot)
                            $snapshot.users[0].permissions[0] =
                                'Unknown.Permission'
                        }
                        ExpectedError =
                            'unsupported or duplicate permission'
                    }
                )

                foreach ($case in $cases) {
                    $payload = New-ValidSourceUsersSnapshotPayload
                    & $case.Configure $payload
                    [IO.File]::WriteAllText(
                        $snapshotPath,
                        ($payload | ConvertTo-Json -Depth 20),
                        [Text.UTF8Encoding]::new($false))
                    $expectedSha256 = (
                        Get-FileHash `
                            -LiteralPath $snapshotPath `
                            -Algorithm SHA256
                    ).Hash

                    $rejected = $false
                    try {
                        Import-SourceUsersSnapshot `
                            -Path $snapshotPath `
                            -AllowedRoot $allowedRoot `
                            -ExpectedSha256 $expectedSha256 |
                            Out-Null
                    }
                    catch {
                        $rejected = $true
                        if (
                            [string]$_.Exception.Message -notmatch
                                [regex]::Escape([string]$case.ExpectedError)
                        ) {
                            throw (
                                "Unexpected rejection for $($case.Name): " +
                                $_.Exception.Message)
                        }
                    }
                    if (-not $rejected) {
                        throw "Unsafe snapshot case was accepted: $($case.Name)"
                    }
                }

                Write-Output 'source-users-snapshot-fail-closed-matrix-verified'
            }
            finally {
                Remove-Item `
                    -LiteralPath $allowedRoot `
                    -Recurse `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "source-users-snapshot-fail-closed-matrix-verified",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveSourceUsersSnapshot_PrefersFileSnapshotWithoutCallingApi()
    {
        const string secret = "stored-password-must-not-reach-snapshot-log";
        const string userCanary = "snapshot-user-must-not-reach-metadata-log";
        var result = await RunPreparationFunctionsAsync(
            ["Resolve-SourceUsersSnapshot"],
            $$"""
            $script:apiCallCount = 0
            $script:writtenContent = $null

            function Get-SourceUsersFromApi {
                $script:apiCallCount++
                throw 'The source users API must not be called.'
            }

            function Write-Utf8File {
                param(
                    [string]$Path,
                    [string]$Content
                )
                $script:writtenContent = $Content
            }

            $fileSnapshot = [pscustomobject]@{
                SourceKind = 'georaeplan-user-permission-snapshot-v1'
                GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
                IsComplete = $true
                UserCount = 1
                PermissionCount = 1
                ScopeCounts = @(
                    [pscustomobject]@{
                        tenantCode = 'USENET_GROUP'
                        officeCode = 'USENET'
                        role = 'Admin'
                        scopeType = 'Admin'
                        isActive = $true
                        userCount = 1
                        permissionCount = 1
                    }
                )
                CanonicalSha256 = ('B' * 64)
                SnapshotSha256 = ('A' * 64)
                Users = @(
                    [pscustomobject]@{
                        username = '{{userCanary}}'
                        role = 'Admin'
                        tenantCode = 'USENET_GROUP'
                        officeCode = 'USENET'
                        scopeType = 'Admin'
                        isActive = $true
                        permissions = @('Settings.Edit')
                    }
                )
            }
            $resolved = Resolve-SourceUsersSnapshot `
                -FileSnapshot $fileSnapshot `
                -BaseUrl 'https://must-not-be-called.invalid' `
                -StoredCredentials @(
                    [pscustomobject]@{
                        Username = 'stored-user'
                        Password = '{{secret}}'
                    }
                ) `
                -LogPath 'unused-by-test.log'

            if (
                $script:apiCallCount -ne 0 -or
                -not [object]::ReferenceEquals($fileSnapshot, $resolved) -or
                [string]::IsNullOrWhiteSpace($script:writtenContent) -or
                $script:writtenContent -notmatch
                    'georaeplan-user-permission-snapshot-v1' -or
                $script:writtenContent -match [regex]::Escape('{{secret}}') -or
                $script:writtenContent -match
                    [regex]::Escape('{{userCanary}}') -or
                $script:writtenContent -match '"users"\s*:'
            ) {
                throw 'The file snapshot did not bypass the API safely.'
            }

            Write-Output 'file-source-users-snapshot-preferred'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "file-source-users-snapshot-preferred",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secret,
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            userCanary,
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceUsersSnapshot_StaticFailClosedContractsRemainPresent()
    {
        var preparationSource = File.ReadAllText(ResolvePreparationScript());
        var importStart = preparationSource.IndexOf(
            "function Import-SourceUsersSnapshot",
            StringComparison.Ordinal);
        var resolveStart = preparationSource.IndexOf(
            "function Resolve-SourceUsersSnapshot",
            importStart,
            StringComparison.Ordinal);
        var resolveEnd = preparationSource.IndexOf(
            "function Get-FallbackOperationalUsers",
            resolveStart,
            StringComparison.Ordinal);
        Assert.True(
            importStart >= 0 && resolveStart > importStart && resolveEnd > resolveStart,
            "The source users snapshot import/selection functions were not found.");

        var importSource = preparationSource[importStart..resolveStart];
        Assert.Contains(
            "[string]$SourceUsersSnapshotPath",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "'georaeplan-user-permission-snapshot-v1'",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Parameter(Mandatory = $true)][string]$AllowedRoot",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$SourceUsersSnapshotSha256",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$ExpectedSha256",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Source users snapshot SHA-256 does not match the expected value",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileShare]::None",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssertNoDuplicateJsonObjectProperties($jsonText)",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "'scopeCounts'",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "'canonicalSha256'",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "contains missing or unsupported root fields",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "contains missing or unsupported user fields",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "must declare isComplete=true",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "has no active Admin/Admin user",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "contains an unsupported or duplicate permission",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "scopeCounts do not match users",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "canonicalSha256 does not match users",
            importSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SnapshotSha256 = $snapshotSha256",
            importSource,
            StringComparison.Ordinal);

        var resolveSource = preparationSource[resolveStart..resolveEnd];
        var fileSelection = resolveSource.IndexOf(
            "if ($null -ne $FileSnapshot)",
            StringComparison.Ordinal);
        var apiFallback = resolveSource.IndexOf(
            "return Get-SourceUsersFromApi",
            StringComparison.Ordinal);
        Assert.True(
            fileSelection >= 0 && apiFallback > fileSelection,
            "The file snapshot must be selected before the API fallback.");
        Assert.DoesNotContain(
            "$FileSnapshot.Users",
            resolveSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceUsersSnapshot_PermissionCatalogMatchesServerConstants()
    {
        var repositoryRoot = FindRepositoryRoot();
        var preparationSource = File.ReadAllText(ResolvePreparationScript());
        var catalogStart = preparationSource.IndexOf(
            "function Get-SourceUsersSnapshotKnownPermissions",
            StringComparison.Ordinal);
        var catalogEnd = preparationSource.IndexOf(
            "function Get-SourceUsersSnapshotTextSha256",
            catalogStart,
            StringComparison.Ordinal);
        Assert.True(catalogStart >= 0 && catalogEnd > catalogStart);

        var importerPermissions =
            System.Text.RegularExpressions.Regex.Matches(
                    preparationSource[catalogStart..catalogEnd],
                    "'([A-Za-z][A-Za-z0-9]*\\.[A-Za-z][A-Za-z0-9]*)'")
                .Select(match => match.Groups[1].Value)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        var serverSource = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "Server",
                "거래플랜.Server.Api",
                "Security",
                "PermissionNames.cs"));
        var serverPermissions =
            System.Text.RegularExpressions.Regex.Matches(
                    serverSource,
                    "public const string \\w+ = \"([^\"]+)\";")
                .Select(match => match.Groups[1].Value)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

        Assert.NotEmpty(serverPermissions);
        Assert.Equal(serverPermissions, importerPermissions);
    }

    [Fact]
    public async Task ResolveIsolatedUserDefinitions_RejectsUnresolvedPasswordByDefault()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Resolve-IsolatedUserDefinitions"],
            """
            Resolve-IsolatedUserDefinitions `
                -SourceUsers @(
                    [pscustomobject]@{
                        username = 'active-user'
                        role = 'Admin'
                        officeCode = 'USENET'
                        tenantCode = 'USENET_GROUP'
                        scopeType = 'Admin'
                        isActive = $true
                        permissions = @()
                    }
                ) `
                -StoredCredentials @() |
                Out-Null
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "-ResetUnresolvedUserPasswordsForIsolatedTest",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveIsolatedUserDefinitions_ResetsEverySourceUserWithExplicitAllUserMode()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Resolve-IsolatedUserDefinitions"],
            """
            $resolved = @(
                Resolve-IsolatedUserDefinitions `
                    -SourceUsers @(
                        [pscustomobject]@{
                            username = 'active-user'
                            role = 'Admin'
                            officeCode = 'USENET'
                            tenantCode = 'USENET_GROUP'
                            scopeType = 'Admin'
                            isActive = $true
                            permissions = @()
                        },
                        [pscustomobject]@{
                            username = 'inactive-user'
                            role = 'User'
                            officeCode = 'USENET'
                            tenantCode = 'USENET_GROUP'
                            scopeType = 'OfficeOnly'
                            isActive = $false
                            permissions = @()
                        }
                    ) `
                    -StoredCredentials @() `
                    -ResetAllPasswords
            )
            if (
                $resolved.Count -ne 2 -or
                @($resolved | Where-Object {
                    [string]$_.Password -ne '1234' -or
                    -not [bool]$_.PasswordWasReset
                }).Count -ne 0
            ) {
                throw 'Explicit all-user password reset was incomplete.'
            }
            Write-Output 'explicit-all-user-password-reset'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "explicit-all-user-password-reset",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$true", "$false")]
    [InlineData("$false", "$true")]
    public async Task ResolveIsolatedUserDefinitions_AllUserResetRejectsCredentialsOrUnresolvedMode(
        string includeCredential,
        string includeUnresolvedMode)
    {
        var result = await RunPreparationFunctionsAsync(
            ["Resolve-IsolatedUserDefinitions"],
            $$"""
            $credentials = if ({{includeCredential}}) {
                @([pscustomobject]@{ Username = 'admin'; Password = 'secret' })
            }
            else { @() }
            $parameters = @{
                SourceUsers = @(
                    [pscustomobject]@{
                        username = 'admin'
                        role = 'Admin'
                        officeCode = 'USENET'
                        tenantCode = 'USENET_GROUP'
                        scopeType = 'Admin'
                        isActive = $true
                        permissions = @()
                    }
                )
                StoredCredentials = $credentials
                ResetAllPasswords = $true
                ResetUnresolvedPasswords = {{includeUnresolvedMode}}
            }
            Resolve-IsolatedUserDefinitions @parameters | Out-Null
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(
            "secret",
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://127.0.0.1:19080/api")]
    [InlineData("http://user@127.0.0.1:19080")]
    [InlineData("http://127.0.0.1:19080/?query=1")]
    [InlineData("http://127.0.0.1:19080/#fragment")]
    public async Task IsolatedServerUserRestCalls_RejectUnsafeBaseUrlBeforeHttp(
        string targetBaseUrl)
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "Assert-IsolatedLoopbackBaseUrl",
                "Is-AdminUsername",
                "Get-IsolatedVerificationAdmin",
                "Get-NormalizedPermissionSet",
                "Get-IsolatedUserStateDifferences",
                "Assert-IsolatedServerUserState",
                "Sync-IsolatedServerUsers"
            ],
            $$"""
            $script:httpCalls = 0
            function Invoke-RestMethod {
                $script:httpCalls++
                throw 'HTTP must not be called'
            }
            $users = @(
                [pscustomobject]@{
                    Username = 'admin'
                    Password = '1234'
                    Role = 'Admin'
                    OfficeCode = 'USENET'
                    TenantCode = 'USENET_GROUP'
                    ScopeType = 'Admin'
                    IsActive = $true
                    Permissions = @()
                }
            )
            foreach ($action in @('sync', 'assert')) {
                try {
                    if ($action -eq 'sync') {
                        Sync-IsolatedServerUsers `
                            -TargetBaseUrl '{{targetBaseUrl}}' `
                            -AdminPassword '1234' `
                            -Users $users `
                            -LogPath 'D:\unused.json'
                    }
                    else {
                        Assert-IsolatedServerUserState `
                            -TargetBaseUrl '{{targetBaseUrl}}' `
                            -AdminPassword '1234' `
                            -Users $users `
                            -LogPath 'D:\unused.json'
                    }
                    throw 'unsafe URL was accepted'
                }
                catch {
                    if ($_.Exception.Message -eq 'HTTP must not be called') {
                        throw
                    }
                }
            }
            if ($script:httpCalls -ne 0) {
                throw "Unexpected HTTP call count: $script:httpCalls"
            }
            Write-Output 'unsafe-loopback-target-rejected-before-http'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "unsafe-loopback-target-rejected-before-http",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsolatedLoopbackBaseUrl_AcceptsRootLoopbackHttpAndHttps()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Assert-IsolatedLoopbackBaseUrl"],
            """
            $accepted = @(
                Assert-IsolatedLoopbackBaseUrl -BaseUrl 'http://127.0.0.1:19080'
                Assert-IsolatedLoopbackBaseUrl -BaseUrl 'https://localhost:19081/'
                Assert-IsolatedLoopbackBaseUrl -BaseUrl 'http://[::1]:19082'
            )
            if ($accepted.Count -ne 3) {
                throw 'Expected loopback roots were rejected.'
            }
            Write-Output 'loopback-root-accepted'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "loopback-root-accepted",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveIsolatedUserDefinitions_RejectsMalformedSourceUser()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Resolve-IsolatedUserDefinitions"],
            """
            Resolve-IsolatedUserDefinitions `
                -SourceUsers @(
                    [pscustomobject]@{
                        username = ''
                        role = 'User'
                        officeCode = 'USENET'
                        tenantCode = 'USENET_GROUP'
                        scopeType = 'OfficeOnly'
                        isActive = $true
                        permissions = @()
                    }
                ) `
                -StoredCredentials @() `
                -ResetUnresolvedPasswords |
                Out-Null
            """);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ResolveIsolatedSourceUsers_FailsClosedWhenStoredCredentialsExist()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Get-FallbackOperationalUsers", "Resolve-IsolatedSourceUsers"],
            """
            $credentials = @(
                [pscustomobject]@{
                    Username = 'stored-user'
                    Password = 'must-not-be-printed'
                }
            )
            Resolve-IsolatedSourceUsers `
                -SourceUsersSnapshot $null `
                -StoredCredentials $credentials |
                Out-Null
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "권한이 누락될 수 있어 복원을 중단",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "must-not-be-printed",
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveIsolatedSourceUsers_PreservesFetchedSourceUsers()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Get-FallbackOperationalUsers", "Resolve-IsolatedSourceUsers"],
            """
            $snapshot = [pscustomobject]@{
                IsComplete = $true
                Users = @(
                    [pscustomobject]@{
                        username = 'source-user'
                    }
                )
            }
            $resolved = @(
                Resolve-IsolatedSourceUsers `
                    -SourceUsersSnapshot $snapshot `
                    -StoredCredentials @(
                        [pscustomobject]@{
                            Username = 'stored-user'
                            Password = 'secret'
                        }
                    )
            )
            if (
                $resolved.Count -ne 1 -or
                [string]$resolved[0].username -ne 'source-user'
            ) {
                throw 'The fetched source user set was replaced.'
            }
            Write-Output 'source-users-preserved'
            """);

        Assert.True(
            result.ExitCode == 0,
            BuildFailureMessage(result));
        Assert.Contains(
            "source-users-preserved",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveIsolatedSourceUsers_RejectsUnavailableSourceByDefault()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Get-FallbackOperationalUsers", "Resolve-IsolatedSourceUsers"],
            """
            Resolve-IsolatedSourceUsers `
                -SourceUsersSnapshot $null `
                -StoredCredentials @() |
                Out-Null
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "-AllowFallbackOperationalUsers",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveIsolatedSourceUsers_AllowsOnlyExplicitFallback()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Get-FallbackOperationalUsers", "Resolve-IsolatedSourceUsers"],
            """
            $resolved = @(
                Resolve-IsolatedSourceUsers `
                    -SourceUsersSnapshot $null `
                    -StoredCredentials @() `
                    -AllowFallback
            )
            if ($resolved.Count -ne 4) {
                throw "Unexpected fallback user count: $($resolved.Count)"
            }
            Write-Output 'explicit-fallback-only'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "explicit-fallback-only",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveIsolatedSourceUsers_RejectsFallbackWhenStoredCredentialsExist()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Get-FallbackOperationalUsers", "Resolve-IsolatedSourceUsers"],
            """
            Resolve-IsolatedSourceUsers `
                -SourceUsersSnapshot $null `
                -StoredCredentials @(
                    [pscustomobject]@{
                        Username = 'stored-user'
                        Password = 'must-not-be-printed'
                    }
                ) `
                -AllowFallback |
                Out-Null
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(
            "must-not-be-printed",
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveIsolatedSourceUsers_RejectsPartialNonEmptyResponse()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Get-FallbackOperationalUsers", "Resolve-IsolatedSourceUsers"],
            """
            $snapshot = [pscustomobject]@{
                IsComplete = $false
                Users = @(
                    [pscustomobject]@{
                        username = 'partial-user'
                    }
                )
            }
            Resolve-IsolatedSourceUsers `
                -SourceUsersSnapshot $snapshot `
                -StoredCredentials @() |
                Out-Null
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "전체 목록임을 확인할 수 없어",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserStateComparison_DetectsPermissionAndScopeDifferences()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "Get-NormalizedPermissionSet",
                "Get-IsolatedUserStateDifferences"
            ],
            """
            $expected = [pscustomobject]@{
                Role = 'Admin'
                TenantCode = 'TENANT'
                OfficeCode = 'OFFICE'
                ScopeType = 'TenantAll'
                IsActive = $true
                Permissions = @('Settings.Edit', 'Rental.ViewAll')
            }
            $actual = [pscustomobject]@{
                role = 'admin'
                tenantCode = 'tenant'
                officeCode = 'office'
                scopeType = 'OfficeOnly'
                isActive = $true
                permissions = @('Settings.Edit')
            }
            $differences = @(
                Get-IsolatedUserStateDifferences `
                    -Expected $expected `
                    -Actual $actual
            )
            if (
                $differences.Count -ne 2 -or
                $differences -notcontains 'ScopeType' -or
                $differences -notcontains 'Permissions'
            ) {
                throw (
                    'Unexpected user-state differences: ' +
                    ($differences -join ','))
            }
            Write-Output ($differences -join ',')
            """);

        Assert.True(
            result.ExitCode == 0,
            BuildFailureMessage(result));
        Assert.Contains("ScopeType", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Permissions", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserStateVerification_AcceptsRejectedLoginForInactiveAccount()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "Is-AdminUsername",
                "Get-IsolatedVerificationAdmin",
                "Get-NormalizedPermissionSet",
                "Get-IsolatedUserStateDifferences",
                "Assert-IsolatedLoopbackBaseUrl",
                "Assert-IsolatedServerUserState"
            ],
            """
            Add-Type -TypeDefinition @'
            using System.Net;

            public sealed class InactiveAccountResponse : WebResponse
            {
                private readonly HttpStatusCode _statusCode;

                public InactiveAccountResponse(HttpStatusCode statusCode)
                {
                    _statusCode = statusCode;
                }

                public HttpStatusCode StatusCode
                {
                    get { return _statusCode; }
                }
            }

            public sealed class InactiveAccountException : WebException
            {
                public InactiveAccountException(HttpStatusCode statusCode)
                    : base(
                        "expected inactive-account rejection",
                        null,
                        WebExceptionStatus.ProtocolError,
                        new InactiveAccountResponse(statusCode))
                {
                }
            }
            '@

            $script:actualUsers = @(
                [pscustomobject]@{
                    username = 'admin'
                    role = 'Admin'
                    tenantCode = 'TENANT'
                    officeCode = 'OFFICE'
                    scopeType = 'Admin'
                    isActive = $true
                    permissions = @()
                    revision = 11
                },
                [pscustomobject]@{
                    username = 'disabled-user'
                    role = 'User'
                    tenantCode = 'TENANT'
                    officeCode = 'OFFICE'
                    scopeType = 'OfficeOnly'
                    isActive = $false
                    permissions = @()
                }
            )

            function Invoke-RestMethod {
                param(
                    [string]$Method,
                    [string]$Uri,
                    [string]$ContentType,
                    [string]$Body,
                    [hashtable]$Headers,
                    [int]$TimeoutSec
                )

                if ($Method -eq 'Get') {
                    return $script:actualUsers
                }

                $request = $Body | ConvertFrom-Json
                if ([string]$request.username -eq 'disabled-user') {
                    throw [InactiveAccountException]::new(
                        [System.Net.HttpStatusCode]::Unauthorized)
                }

                return [pscustomobject]@{
                    token = 'admin-token'
                    user = [pscustomobject]@{
                        username = 'admin'
                        role = 'Admin'
                        tenantCode = 'TENANT'
                        officeCode = 'OFFICE'
                        scopeType = 'Admin'
                        permissions = @()
                    }
                }
            }

            function Write-Utf8File {
                param(
                    [string]$Path,
                    [string]$Content
                )

                [IO.File]::WriteAllText(
                    $Path,
                    $Content,
                    [Text.UTF8Encoding]::new($false))
            }

            $desiredUsers = @(
                [pscustomobject]@{
                    Username = 'admin'
                    Role = 'Admin'
                    TenantCode = 'TENANT'
                    OfficeCode = 'OFFICE'
                    ScopeType = 'Admin'
                    IsActive = $true
                    Permissions = @()
                    Password = 'bootstrap-admin-password'
                },
                [pscustomobject]@{
                    Username = 'disabled-user'
                    Role = 'User'
                    TenantCode = 'TENANT'
                    OfficeCode = 'OFFICE'
                    ScopeType = 'OfficeOnly'
                    IsActive = $false
                    Permissions = @()
                    Password = 'disabled-password'
                }
            )

            Assert-IsolatedServerUserState `
                -TargetBaseUrl 'http://127.0.0.1:1' `
                -AdminPassword 'bootstrap-admin-password' `
                -Users $desiredUsers `
                -LogPath (Join-Path $PSScriptRoot 'user-state.json')
            Write-Output 'inactive-rejection-accepted'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "inactive-rejection-accepted",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserStateVerification_RejectsActiveLoginWithoutToken()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "Is-AdminUsername",
                "Get-IsolatedVerificationAdmin",
                "Get-NormalizedPermissionSet",
                "Get-IsolatedUserStateDifferences",
                "Assert-IsolatedLoopbackBaseUrl",
                "Assert-IsolatedServerUserState"
            ],
            """
            $script:loginCount = 0
            $script:actualUsers = @(
                [pscustomobject]@{
                    username = 'admin'
                    role = 'Admin'
                    tenantCode = 'TENANT'
                    officeCode = 'OFFICE'
                    scopeType = 'Admin'
                    isActive = $true
                    permissions = @()
                }
            )

            function Invoke-RestMethod {
                param(
                    [string]$Method,
                    [string]$Uri,
                    [string]$ContentType,
                    [string]$Body,
                    [hashtable]$Headers,
                    [int]$TimeoutSec
                )

                if ($Method -eq 'Get') {
                    return $script:actualUsers
                }

                $script:loginCount++
                return [pscustomobject]@{
                    token = if ($script:loginCount -eq 1) {
                        'bootstrap-admin-token'
                    }
                    else {
                        ''
                    }
                    user = [pscustomobject]@{
                        username = 'admin'
                        role = 'Admin'
                        tenantCode = 'TENANT'
                        officeCode = 'OFFICE'
                        scopeType = 'Admin'
                        permissions = @()
                    }
                }
            }

            function Write-Utf8File {
                param(
                    [string]$Path,
                    [string]$Content
                )
            }

            Assert-IsolatedServerUserState `
                -TargetBaseUrl 'http://127.0.0.1:1' `
                -AdminPassword 'must-not-be-printed' `
                -Users @(
                    [pscustomobject]@{
                        Username = 'admin'
                        Role = 'Admin'
                        TenantCode = 'TENANT'
                        OfficeCode = 'OFFICE'
                        ScopeType = 'Admin'
                        IsActive = $true
                        Permissions = @()
                        Password = 'must-not-be-printed'
                    }
                ) `
                -LogPath 'D:\active-login-no-token.json'
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(
            "must-not-be-printed",
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserSync_UsesNonLiteralSystemAdminAndDeletesEveryUnexpectedSeedUser()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "Is-AdminUsername",
                "Get-IsolatedVerificationAdmin",
                "Get-NormalizedPermissionSet",
                "Get-IsolatedUserStateDifferences",
                "Assert-IsolatedLoopbackBaseUrl",
                "Assert-IsolatedServerUserState",
                "Sync-IsolatedServerUsers"
            ],
            """
            $script:users = @(
                [pscustomobject]@{
                    id = 'seed-admin-id'
                    username = 'admin'
                    role = 'Admin'
                    tenantCode = 'TENANT'
                    officeCode = 'OFFICE'
                    scopeType = 'Admin'
                    isActive = $true
                    permissions = @()
                    revision = 11
                },
                [pscustomobject]@{
                    id = 'seed-usenet-id'
                    username = 'usenet'
                    role = 'Admin'
                    tenantCode = 'TENANT'
                    officeCode = 'OFFICE'
                    scopeType = 'TenantAll'
                    isActive = $true
                    permissions = @()
                    revision = 12
                }
            )

            function Invoke-RestMethod {
                param(
                    [string]$Method,
                    [string]$Uri,
                    [string]$ContentType,
                    [string]$Body,
                    [hashtable]$Headers,
                    [int]$TimeoutSec
                )

                if ($Uri.EndsWith('/auth/login')) {
                    $request = $Body | ConvertFrom-Json
                    $loginUser = @(
                        $script:users |
                            Where-Object {
                                [string]$_.username -eq
                                    [string]$request.username
                            } |
                            Select-Object -First 1
                    )
                    if ($loginUser.Count -ne 1) {
                        throw 'login rejected'
                    }
                    return [pscustomobject]@{
                        token = 'fixture-token'
                        user = $loginUser[0]
                    }
                }

                if ($Method -eq 'Get' -and $Uri.EndsWith('/users')) {
                    # Match Windows PowerShell 5.1 Invoke-RestMethod behavior:
                    # a JSON array can arrive as one pipeline object.
                    Write-Output -NoEnumerate @($script:users)
                    return
                }

                if ($Method -eq 'Post' -and $Uri.EndsWith('/users')) {
                    $request = $Body | ConvertFrom-Json
                    $script:users += [pscustomobject]@{
                        id = 'rootops-id'
                        username = [string]$request.username
                        role = [string]$request.role
                        tenantCode = [string]$request.tenantCode
                        officeCode = [string]$request.officeCode
                        scopeType = [string]$request.scopeType
                        isActive = [bool]$request.isActive
                        permissions = @($request.permissions)
                        revision = 13
                    }
                    return [pscustomobject]@{}
                }

                if ($Method -eq 'Delete') {
                    $parsedUri = [Uri]$Uri
                    $id = $parsedUri.AbsolutePath.Substring(
                        $parsedUri.AbsolutePath.LastIndexOf('/') + 1)
                    $target = @(
                        $script:users |
                            Where-Object {
                                [string]$_.id -eq $id
                            } |
                            Select-Object -First 1
                    )
                    $expectedRevision = [long](
                        [System.Web.HttpUtility]::ParseQueryString(
                            $parsedUri.Query)['expectedRevision'])
                    if (
                        $target.Count -ne 1 -or
                        $expectedRevision -ne
                            [long]$target[0].revision
                    ) {
                        throw 'Unexpected delete expectedRevision.'
                    }
                    $script:users = @(
                        $script:users |
                            Where-Object { [string]$_.id -ne $id }
                    )
                    return [pscustomobject]@{}
                }

                throw "Unexpected fixture request: $Method $Uri"
            }

            function Write-Utf8File {
                param(
                    [string]$Path,
                    [string]$Content
                )
                $script:verificationLog = $Content
            }

            $desiredUsers = @(
                [pscustomobject]@{
                    Username = 'rootops'
                    Role = 'Admin'
                    TenantCode = 'TENANT'
                    OfficeCode = 'OFFICE'
                    ScopeType = 'Admin'
                    IsActive = $true
                    Permissions = @()
                    Password = 'must-not-be-printed'
                }
            )

            Sync-IsolatedServerUsers `
                -TargetBaseUrl 'http://127.0.0.1:1' `
                -AdminPassword 'bootstrap-password' `
                -Users $desiredUsers `
                -LogPath 'D:\nonliteral-admin-sync.json'

            if (
                $script:users.Count -ne 1 -or
                [string]$script:users[0].username -ne 'rootops'
            ) {
                throw (
                    'Unexpected seed users survived cleanup: ' +
                    (($script:users | ForEach-Object username) -join ','))
            }
            if (
                $script:verificationLog -notmatch '"username":\s+"admin"' -or
                $script:verificationLog -notmatch '"username":\s+"usenet"'
            ) {
                throw 'Unexpected-user cleanup actions were not recorded.'
            }
            Write-Output 'nonliteral-admin-exact-set'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "nonliteral-admin-exact-set",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "must-not-be-printed",
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserSync_ReauthenticatesStoredBootstrapAdminCasingAfterPasswordInvalidatesToken()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "Is-AdminUsername",
                "Get-IsolatedVerificationAdmin",
                "Get-NormalizedPermissionSet",
                "Get-IsolatedUserStateDifferences",
                "Assert-IsolatedLoopbackBaseUrl",
                "Assert-IsolatedServerUserState",
                "Sync-IsolatedServerUsers"
            ],
            """
            $script:users = @(
                [pscustomobject]@{
                    id = 'seed-admin-id'
                    username = 'admin'
                    role = 'Admin'
                    tenantCode = 'OLD-TENANT'
                    officeCode = 'OLD-OFFICE'
                    scopeType = 'Admin'
                    isActive = $true
                    permissions = @('OLD_PERMISSION')
                    revision = 21
                }
            )
            $script:passwordUpdated = $false
            $script:passwordReauthenticated = $false
            $script:structureUpdated = $false

            function Invoke-RestMethod {
                param(
                    [string]$Method,
                    [string]$Uri,
                    [string]$ContentType,
                    [string]$Body,
                    [hashtable]$Headers,
                    [int]$TimeoutSec
                )

                if ($Uri.EndsWith('/auth/login')) {
                    $request = $Body | ConvertFrom-Json
                    $loginUser = @(
                        $script:users |
                            Where-Object {
                                [string]$_.username -ceq
                                    [string]$request.username
                            } |
                            Select-Object -First 1
                    )
                    if ($loginUser.Count -ne 1) {
                        throw 'login rejected'
                    }
                    $token = if (
                        [string]$request.username -ceq 'admin' -and
                        -not $script:passwordUpdated
                    ) {
                        'bootstrap-token'
                    }
                    elseif (
                        [string]$request.username -ceq 'admin' -and
                        $script:passwordUpdated -and
                        -not $script:structureUpdated
                    ) {
                        if (
                            [string]$request.password -ne
                                'restored-admin-secret'
                        ) {
                            throw 'The updated admin password was not used.'
                        }
                        $script:passwordReauthenticated = $true
                        'password-refreshed-token'
                    }
                    else {
                        'restored-token'
                    }
                    return [pscustomobject]@{
                        token = $token
                        user = $loginUser[0]
                    }
                }

                $authorization = [string]$Headers.Authorization
                if (
                    $authorization -eq 'Bearer bootstrap-token' -and
                    $script:passwordUpdated
                ) {
                    throw 'The password update invalidated the bootstrap token.'
                }

                if ($Method -eq 'Get' -and $Uri.EndsWith('/users')) {
                    # Match Windows PowerShell 5.1 Invoke-RestMethod behavior:
                    # a JSON array can arrive as one pipeline object.
                    Write-Output -NoEnumerate @($script:users)
                    return
                }

                if ($Method -eq 'Post' -and $Uri.EndsWith('/users')) {
                    $request = $Body | ConvertFrom-Json
                    $script:users += [pscustomobject]@{
                        id = 'rootops-id'
                        username = [string]$request.username
                        role = [string]$request.role
                        tenantCode = [string]$request.tenantCode
                        officeCode = [string]$request.officeCode
                        scopeType = [string]$request.scopeType
                        isActive = [bool]$request.isActive
                        permissions = @($request.permissions)
                        revision = 30
                    }
                    return [pscustomobject]@{}
                }

                if (
                    $Method -eq 'Put' -and
                    $Uri.EndsWith('/seed-admin-id/password')
                ) {
                    if ($script:structureUpdated) {
                        throw 'Password update ran after token invalidation.'
                    }
                    if (
                        $authorization -ne 'Bearer bootstrap-token'
                    ) {
                        throw 'Password update did not use the bootstrap token.'
                    }
                    $request = $Body | ConvertFrom-Json
                    if ([long]$request.expectedRevision -ne 21) {
                        throw 'Password update did not use the current revision.'
                    }
                    $script:users[0].revision = 22
                    $script:passwordUpdated = $true
                    return [pscustomobject]@{}
                }

                if (
                    $Method -eq 'Put' -and
                    $Uri.EndsWith('/seed-admin-id')
                ) {
                    if (-not $script:passwordUpdated) {
                        throw 'Claims update ran before the password update.'
                    }
                    if (
                        -not $script:passwordReauthenticated -or
                        $authorization -ne
                            'Bearer password-refreshed-token'
                    ) {
                        throw (
                            'Claims update did not use a token refreshed ' +
                            'after the password update.')
                    }
                    $request = $Body | ConvertFrom-Json
                    if ([long]$request.expectedRevision -ne 22) {
                        throw 'Claims update did not use the refreshed revision.'
                    }
                    $script:users[0] = [pscustomobject]@{
                        id = 'seed-admin-id'
                        username = [string]$request.username
                        role = [string]$request.role
                        tenantCode = [string]$request.tenantCode
                        officeCode = [string]$request.officeCode
                        scopeType = [string]$request.scopeType
                        isActive = [bool]$request.isActive
                        permissions = @($request.permissions)
                        revision = 23
                    }
                    $script:structureUpdated = $true
                    return [pscustomobject]@{}
                }

                throw "Unexpected fixture request: $Method $Uri"
            }

            function Write-Utf8File {
                param(
                    [string]$Path,
                    [string]$Content
                )
            }

            $desiredUsers = @(
                [pscustomobject]@{
                    Username = 'rootops'
                    Role = 'Admin'
                    TenantCode = 'TENANT'
                    OfficeCode = 'OFFICE'
                    ScopeType = 'Admin'
                    IsActive = $true
                    Permissions = @()
                    Password = 'rootops-secret'
                },
                [pscustomobject]@{
                    Username = 'Admin'
                    Role = 'Admin'
                    TenantCode = 'NEW-TENANT'
                    OfficeCode = 'NEW-OFFICE'
                    ScopeType = 'Admin'
                    IsActive = $true
                    Permissions = @('NEW_PERMISSION')
                    Password = 'restored-admin-secret'
                }
            )

            Sync-IsolatedServerUsers `
                -TargetBaseUrl 'http://127.0.0.1:1' `
                -AdminPassword 'bootstrap-secret' `
                -Users $desiredUsers `
                -LogPath 'D:\bootstrap-token-order.json'

            if (
                -not $script:passwordUpdated -or
                -not $script:passwordReauthenticated -or
                -not $script:structureUpdated
            ) {
                throw 'The existing bootstrap admin was not fully restored.'
            }
            Write-Output 'bootstrap-password-reauthenticated-before-claims'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "bootstrap-password-reauthenticated-before-claims",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "rootops-secret",
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "restored-admin-secret",
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bootstrap-secret",
            result.Stdout + result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitHttpReady_RequiresExactHealthyResponse()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Wait-HttpReady"],
            """
            $script:statusCode = 404
            $script:content = '{"status":"ok"}'
            function Invoke-WebRequest {
                param(
                    [string]$Uri,
                    [switch]$UseBasicParsing,
                    [int]$TimeoutSec
                )
                return [pscustomobject]@{
                    StatusCode = $script:statusCode
                    Content = $script:content
                }
            }

            if (Wait-HttpReady -Url 'http://127.0.0.1:1/healthz' -TimeoutSeconds 1) {
                throw 'HTTP 404 was accepted as healthy.'
            }

            $script:statusCode = 200
            $script:content = '{"status":"not-ok"}'
            if (Wait-HttpReady -Url 'http://127.0.0.1:1/healthz' -TimeoutSeconds 1) {
                throw 'An unexpected health body was accepted.'
            }

            $script:content = '{"status":"ok"}'
            if (-not (Wait-HttpReady -Url 'http://127.0.0.1:1/healthz' -TimeoutSeconds 1)) {
                throw 'The expected healthy response was rejected.'
            }
            Write-Output 'exact-health-contract'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "exact-health-contract",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppSnapshotManifest_VerifiesCompleteNonPrimaryDatabaseTreeAndRejectsChanges()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "Get-AppSnapshotFileManifest",
                "Assert-AppSnapshotFileManifestsEqual",
                "Get-AppSnapshotFileManifestDigest"
            ],
            """
            $sourceRoot = Join-Path $PSScriptRoot 'manifest-source'
            $targetRoot = Join-Path $PSScriptRoot 'manifest-target'
            foreach ($root in @($sourceRoot, $targetRoot)) {
                foreach ($child in @(
                    'attachments',
                    'settings',
                    'logs',
                    'backup',
                    'diagnostics',
                    'temp',
                    'data'
                )) {
                    New-Item `
                        -ItemType Directory `
                        -Path (Join-Path $root $child) `
                        -Force |
                        Out-Null
                }
            }

            $snapshotFiles = @(
                'attachments\invoice.bin',
                'settings\ui.json',
                'backup\old.bin',
                'backup\거래플랜.db',
                'diagnostics\trace.txt',
                'data\fixture.db-wal'
            )
            $volatileSnapshotFiles = @(
                'logs\runtime.log',
                'temp\scratch.tmp'
            )
            foreach ($relativePath in $snapshotFiles) {
                $sourcePath = Join-Path $sourceRoot $relativePath
                $targetPath = Join-Path $targetRoot $relativePath
                [IO.File]::WriteAllText(
                    $sourcePath,
                    "snapshot:$relativePath")
                Copy-Item `
                    -LiteralPath $sourcePath `
                    -Destination $targetPath `
                    -Force
                [IO.File]::SetLastWriteTimeUtc(
                    $targetPath,
                    [IO.File]::GetLastWriteTimeUtc($sourcePath))
            }
            foreach ($primaryDatabasePath in @(
                'data\거래플랜.db',
                'data\거래플랜.db-wal',
                'data\거래플랜.db-shm',
                'data\거래플랜.db-journal'
            )) {
                [IO.File]::WriteAllText(
                    (Join-Path $sourceRoot $primaryDatabasePath),
                    'primary-database-file')
            }

            $sourceBefore = @(
                Get-AppSnapshotFileManifest -Root $sourceRoot
            )
            $targetBefore = @(
                Get-AppSnapshotFileManifest -Root $targetRoot
            )
            if (
                $sourceBefore.Count -ne $snapshotFiles.Count -or
                $targetBefore.Count -ne $snapshotFiles.Count
            ) {
                throw 'The managed non-primary-DB AppData file set was not exact.'
            }
            foreach ($relativePath in $snapshotFiles) {
                if (
                    @($sourceBefore.RelativePath) -notcontains $relativePath -or
                    @($targetBefore.RelativePath) -notcontains $relativePath
                ) {
                    throw "The AppData snapshot omitted a file: $relativePath"
                }
            }
            foreach ($relativePath in $volatileSnapshotFiles) {
                if (
                    @($sourceBefore.RelativePath) -contains $relativePath -or
                    @($targetBefore.RelativePath) -contains $relativePath
                ) {
                    throw "The AppData manifest included a volatile file: $relativePath"
                }
            }
            Assert-AppSnapshotFileManifestsEqual `
                -Expected $sourceBefore `
                -Actual $targetBefore `
                -Context 'fixture-copy'
            if (
                (Get-AppSnapshotFileManifestDigest `
                    -Manifest $sourceBefore) -ne
                (Get-AppSnapshotFileManifestDigest `
                    -Manifest $targetBefore)
            ) {
                throw 'Equal manifests produced different digests.'
            }

            [IO.File]::WriteAllText(
                (Join-Path $targetRoot 'settings\ui.json'),
                'tampered-target')
            $targetAfter = @(
                Get-AppSnapshotFileManifest -Root $targetRoot
            )
            $targetRejected = $false
            try {
                Assert-AppSnapshotFileManifestsEqual `
                    -Expected $sourceBefore `
                    -Actual $targetAfter `
                    -Context 'fixture-tamper'
            }
            catch {
                $targetRejected = $true
            }
            if (-not $targetRejected) {
                throw 'A changed target AppData file was accepted.'
            }

            [IO.File]::WriteAllText(
                (Join-Path $sourceRoot 'attachments\invoice.bin'),
                'changed-source')
            $sourceAfter = @(
                Get-AppSnapshotFileManifest -Root $sourceRoot
            )
            $sourceRejected = $false
            try {
                Assert-AppSnapshotFileManifestsEqual `
                    -Expected $sourceBefore `
                    -Actual $sourceAfter `
                    -Context 'fixture-source-change'
            }
            catch {
                $sourceRejected = $true
            }
            if (-not $sourceRejected) {
                throw 'A concurrently changed source file was accepted.'
            }
            Write-Output 'complete-appdata-manifest-verified'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "complete-appdata-manifest-verified",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyCurrentAppSnapshot_CopiesCompleteTreeByteForByte()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "Initialize-TestEnvironmentFinalPathNativeMethods",
                "ConvertTo-NormalizedFullPath",
                "Enter-SourceAppRootIdentityLease",
                "Assert-SourceAppRootIdentityLease",
                "Invoke-RobocopyMirror",
                "Assert-NoSqliteSidecars",
                "Copy-StableStandaloneSqliteSnapshot",
                "Get-AppSnapshotFileManifest",
                "Assert-AppSnapshotFileManifestsEqual",
                "Get-AppSnapshotFileManifestDigest",
                "Copy-CurrentAppSnapshot"
            ],
            """
            $sourceRoot = Join-Path $PSScriptRoot 'complete-copy-source'
            $targetRoot = Join-Path $PSScriptRoot 'complete-copy-target'
            foreach ($relativeDirectory in @(
                'attachments',
                'backup',
                'data',
                'diagnostics',
                'logs',
                'temp'
            )) {
                New-Item `
                    -ItemType Directory `
                    -Path (Join-Path $sourceRoot $relativeDirectory) `
                    -Force |
                    Out-Null
            }
            New-Item -ItemType Directory -Path $targetRoot -Force |
                Out-Null
            [IO.File]::WriteAllText(
                (Join-Path $targetRoot 'stale.txt'),
                'must-be-removed')

            $sourceDatabase =
                Join-Path $sourceRoot 'data\거래플랜.db'
            [IO.File]::WriteAllBytes(
                $sourceDatabase,
                [byte[]](0..255))
            $snapshotFiles = @(
                'attachments\invoice.bin',
                'backup\거래플랜.db',
                'backup\archive.db-wal',
                'diagnostics\trace.json'
            )
            $volatileSnapshotFiles = @(
                'logs\historical.log',
                'temp\captured.tmp'
            )
            foreach ($relativePath in ($snapshotFiles + $volatileSnapshotFiles)) {
                $path = Join-Path $sourceRoot $relativePath
                [IO.File]::WriteAllText(
                    $path,
                    "exact:$relativePath")
            }

            $copyResult = Copy-CurrentAppSnapshot `
                -SourceRoot $sourceRoot `
                -TargetRoot $targetRoot
            if (
                -not [string]::Equals(
                    [string]$copyResult.DatabaseSha256,
                    (Get-FileHash `
                        -LiteralPath $sourceDatabase `
                        -Algorithm SHA256).Hash,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw 'The primary database SHA-256 was not preserved.'
            }
            if (Test-Path -LiteralPath (Join-Path $targetRoot 'stale.txt')) {
                throw 'The target was not mirrored from the source.'
            }
            if (
                [string]$copyResult.DatabaseSnapshotMode -ne
                    'standalone-file-copy'
            ) {
                throw 'The standalone database snapshot mode was not reported.'
            }

            $targetDatabase =
                Join-Path $targetRoot 'data\거래플랜.db'
            if (
                [Convert]::ToBase64String(
                    [IO.File]::ReadAllBytes($sourceDatabase)) -ne
                [Convert]::ToBase64String(
                    [IO.File]::ReadAllBytes($targetDatabase))
            ) {
                throw 'The primary database bytes changed during the copy.'
            }
            foreach ($relativePath in $snapshotFiles) {
                $sourcePath = Join-Path $sourceRoot $relativePath
                $targetPath = Join-Path $targetRoot $relativePath
                if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
                    throw "The snapshot omitted a file: $relativePath"
                }
                if (
                    (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne
                    (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
                ) {
                    throw "The snapshot changed a file: $relativePath"
                }
            }
            foreach ($relativeDirectory in @('logs', 'temp')) {
                $targetDirectory =
                    Join-Path $targetRoot $relativeDirectory
                if (
                    -not (Test-Path `
                        -LiteralPath $targetDirectory `
                        -PathType Container)
                ) {
                    throw (
                        'The volatile AppData directory was not recreated: ' +
                        $relativeDirectory)
                }
                if (
                    @(Get-ChildItem `
                        -LiteralPath $targetDirectory `
                        -Force).Count -ne 0
                ) {
                    throw (
                        'The volatile AppData directory was not empty: ' +
                        $relativeDirectory)
                }
            }
            foreach ($sidecar in @(
                "$targetDatabase-wal",
                "$targetDatabase-shm",
                "$targetDatabase-journal"
            )) {
                if (Test-Path -LiteralPath $sidecar) {
                    throw "The target contains a primary database sidecar: $sidecar"
                }
            }
            Write-Output 'complete-appdata-copy-verified'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "complete-appdata-copy-verified",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyCurrentAppSnapshot_RejectsMissingSourceWithoutReusingTarget()
    {
        var result = await RunPreparationFunctionsAsync(
            ["Copy-CurrentAppSnapshot"],
            """
            $missingSource = Join-Path $PSScriptRoot 'missing-source'
            $targetRoot = Join-Path $PSScriptRoot 'existing-target'
            $targetData = Join-Path $targetRoot 'data'
            New-Item -ItemType Directory -Path $targetData -Force |
                Out-Null
            $sentinel = Join-Path $targetData 'stale.db'
            [IO.File]::WriteAllText($sentinel, 'stale-data-must-not-be-used')

            $rejected = $false
            try {
                Copy-CurrentAppSnapshot `
                    -SourceRoot $missingSource `
                    -TargetRoot $targetRoot |
                    Out-Null
            }
            catch {
                $rejected = $true
                if (
                    [string]$_.Exception.Message -notmatch
                        'Refusing to reuse stale isolated AppData'
                ) {
                    throw
                }
            }

            if (-not $rejected) {
                throw 'A missing source root was accepted.'
            }
            if (
                -not (Test-Path -LiteralPath $sentinel) -or
                [IO.File]::ReadAllText($sentinel) -ne
                    'stale-data-must-not-be-used'
            ) {
                throw 'The rejected operation changed the existing target.'
            }
            Write-Output 'missing-source-failed-closed'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "missing-source-failed-closed",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SeedUserBootstrap_GuardsCredentialsAndVerifiesPostState()
    {
        var repositoryRoot = FindRepositoryRoot();
        var preparationSource = File.ReadAllText(ResolvePreparationScript());
        var programSource = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "SyncDiag", "Program.cs"));

        var isolatedCommandStart = programSource.IndexOf(
            "static bool IsAlwaysIsolatedTestSeedCommand",
            StringComparison.Ordinal);
        var isolatedCommandEnd = programSource.IndexOf(
            "static void AssertIsolatedTestSeedCommandEnvironment",
            isolatedCommandStart,
            StringComparison.Ordinal);
        Assert.True(
            isolatedCommandStart >= 0 && isolatedCommandEnd > isolatedCommandStart,
            "The isolated SyncDiag command list was not found.");
        Assert.Contains(
            "\"stored-credential-envelopes\"",
            programSource[isolatedCommandStart..isolatedCommandEnd],
            StringComparison.Ordinal);

        var storedCredentialsStart = preparationSource.IndexOf(
            "function Initialize-StoredCredentialBoundedProcessCapture",
            StringComparison.Ordinal);
        var storedCredentialsEnd = preparationSource.IndexOf(
            "function Get-SourceUsersFromApi",
            storedCredentialsStart,
            StringComparison.Ordinal);
        Assert.True(
            storedCredentialsStart >= 0 &&
            storedCredentialsEnd > storedCredentialsStart,
            "The stored-credential seed function was not found.");
        var storedCredentialsSource =
            preparationSource[storedCredentialsStart..storedCredentialsEnd];
        Assert.Contains(
            "GEORAEPLAN_TEST_SEED_MODE = '1'",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_TEST_SEED_ROOT = $AppRoot",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreatePipe(",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "StandardOutput = stdoutWrite",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "StandardError = stderrWrite",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateProcessW(",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateSuspended |",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateNoWindow |",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProcThreadAttributeHandleList",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateProcThreadAttribute(",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExtendedStartupInfoPresent",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResumeThread(processInformation.Thread)",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "WaitForSingleObject(",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$TimeoutMilliseconds = 30000",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$MaximumStdoutBytes = 393216",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$MaximumStderrBytes = 8192",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssignProcessToJobObject",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TerminateJobObject",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TerminateProcess(processInformation.Process",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "JobObjectLimitKillOnJobClose",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "BoundedProcessCapture]::Run(",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "stored_credentials_child_output_redacted=True",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "'publish'",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "stored-credential-envelope",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "artifact-manifest.txt",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ArtifactSha256",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Open-StoredCredentialArtifactDirectoryLease",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "New-SecureIsolatedWorkDirectory -Parent $cacheRoot",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeletePrivateTreeAndRoot(",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:UseArtifactsOutput=true",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Open-StoredCredentialArtifactTreeLease",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-StoredCredentialArtifactTreeIntegrity",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileShare]::Read)",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "'.xaml'",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[System.Security.Cryptography.ProtectedData]::Unprotect(",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Array]::Clear(",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Invoke-DotnetWithOutput",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadToEndAsync",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'--no-build'",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bin\\{0}\\net8.0-windows\\SyncDiag.dll",
            storedCredentialsSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Remove-Item",
            storedCredentialsSource,
            StringComparison.Ordinal);

        var verificationStart = preparationSource.IndexOf(
            "function Assert-IsolatedServerUserState",
            StringComparison.Ordinal);
        var verificationEnd = preparationSource.IndexOf(
            "function Sync-IsolatedServerUsers",
            verificationStart,
            StringComparison.Ordinal);
        Assert.True(
            verificationStart >= 0 && verificationEnd > verificationStart,
            "The read-only isolated user verification function was not found.");
        var verificationSource =
            preparationSource[verificationStart..verificationEnd];
        Assert.Contains(
            "$postSyncUsers = @(",
            verificationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-IsolatedUserStateDifferences",
            verificationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$structuralFailures",
            verificationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$loginFailures",
            verificationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "InactiveLoginSucceeded",
            verificationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$statusCode -in @(401, 403)",
            verificationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "differences = @('MissingToken')",
            verificationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "@('usenet', 'itworld', 'yeonsu')",
            verificationSource,
            StringComparison.Ordinal);

        Assert.Contains(
            "Resolve-IsolatedSourceUsers `",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "-AllowFallback:$AllowFallbackOperationalUsers",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "LoginScopeType = $loginScopeType",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "isComplete = $true",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "-EnableSeedUsers $true",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "-EnableSeedUsers $false",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "'SeedUsers__EnableSeedUsers' = 'false'",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "user-bootstrap-after-restart.json",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$response.StatusCode -eq 200",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$healthPayload.status",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains("'/XJ'", preparationSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeLaunchers_RequireFinalCertificationAndMutualPreparationLease()
    {
        var preparationSource = File.ReadAllText(ResolvePreparationScript());
        var runAllStart = preparationSource.IndexOf(
            "$runAllPsContent = @'",
            StringComparison.Ordinal);
        var runAllEnd = preparationSource.IndexOf(
            "\n'@",
            runAllStart,
            StringComparison.Ordinal);
        Assert.True(
            runAllStart >= 0 && runAllEnd > runAllStart,
            "The generated Run-All script was not found.");
        var runAllSource = preparationSource[runAllStart..runAllEnd];
        var runAllResolutionStart = runAllSource.IndexOf(
            "Write-Log 'Resolving app/server files.'",
            StringComparison.Ordinal);
        var runAllResolutionEnd = runAllSource.IndexOf(
            "Write-Log 'App/server files resolved.'",
            runAllResolutionStart,
            StringComparison.Ordinal);
        Assert.True(
            runAllResolutionStart >= 0 &&
            runAllResolutionEnd > runAllResolutionStart,
            "The generated Run-All executable resolution block was not found.");
        var runAllResolutionSource =
            runAllSource[runAllResolutionStart..runAllResolutionEnd];
        var initializationCall = preparationSource.LastIndexOf(
            "Initialize-IsolatedServerData @initializeServerDataParameters",
            StringComparison.Ordinal);
        var launcherWriteCall = preparationSource.IndexOf(
            "Write-TestRunScripts `",
            initializationCall,
            StringComparison.Ordinal);
        var readyMarkerPublish = preparationSource.IndexOf(
            "Publish-TestFileAtomically `",
            launcherWriteCall,
            StringComparison.Ordinal);
        var preparationLeaseOpen = preparationSource.LastIndexOf(
            "$preparationLease = [IO.File]::Open(",
            initializationCall,
            StringComparison.Ordinal);
        var solutionBuild = preparationSource.LastIndexOf(
            "Invoke-Dotnet `",
            initializationCall,
            StringComparison.Ordinal);
        var buildEnvironmentPreflightLeaseOpen = preparationSource.LastIndexOf(
            "Enter-IsolatedBuildEnvironmentPreflightLease `",
            solutionBuild,
            StringComparison.Ordinal);
        var invalidMarkerSet = preparationSource.LastIndexOf(
            "Enter-RuntimeInvalidationMarkerTransactionState `",
            initializationCall,
            StringComparison.Ordinal);
        var preparationGateLeaseOpen = preparationSource.LastIndexOf(
            "Enter-PreparationGateLease -Path $preparationGateLeasePath",
            invalidMarkerSet,
            StringComparison.Ordinal);
        var appOutputReplacement = preparationSource.IndexOf(
            "Invoke-IsolatedRuntimeComponentPromotion `",
            invalidMarkerSet,
            StringComparison.Ordinal);
        var readyMarkerTempWrite = preparationSource.IndexOf(
            "-Path $readyMarkerTempPath `",
            launcherWriteCall,
            StringComparison.Ordinal);
        var runtimePromotionCommit = preparationSource.IndexOf(
            "Complete-IsolatedRuntimePromotionTransaction `",
            readyMarkerPublish,
            StringComparison.Ordinal);
        var invalidMarkerRemoval = preparationSource.IndexOf(
            "$nativeType::DeleteHeldExactSingleLinkRegularFile(",
            runtimePromotionCommit,
            StringComparison.Ordinal);

        Assert.True(
            initializationCall >= 0 &&
            launcherWriteCall > initializationCall &&
            readyMarkerPublish > launcherWriteCall,
            "Runnable launchers or the readiness marker can be published before seed certification.");
        Assert.True(
            invalidMarkerSet >= 0 &&
            appOutputReplacement > invalidMarkerSet,
            "Runtime output replacement can start before the invalid marker is published.");
        Assert.True(
            readyMarkerTempWrite > launcherWriteCall &&
            readyMarkerPublish > readyMarkerTempWrite &&
            runtimePromotionCommit > readyMarkerPublish &&
            invalidMarkerRemoval > runtimePromotionCommit,
            "The ready marker is not atomically published and committed before the exact invalid marker identity is removed.");
        Assert.True(
            buildEnvironmentPreflightLeaseOpen >= 0 &&
            solutionBuild > buildEnvironmentPreflightLeaseOpen,
            "The isolated build-environment lease is not held before the solution build starts.");
        Assert.True(
            preparationGateLeaseOpen >= 0 &&
            invalidMarkerSet > preparationGateLeaseOpen &&
            preparationLeaseOpen > invalidMarkerSet &&
            appOutputReplacement > preparationLeaseOpen,
            "The mutual preparation leases are not held before runtime invalidation and component promotion.");
        Assert.Contains(
            "V1 restore writable roots must stay on D:",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ".georaeplan-runtime-ready",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ".georaeplan-runtime-invalid",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "explicitly invalidated",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "INVALID_MARKER",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ".georaeplan-prepare.lock",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-RuntimeCertification",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "certification_id",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "isolated-original-data-test-password-resets",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Run-IsolatedComponent.ps1",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileShare]::None",
            preparationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Start-Process -FilePath (Join-Path $PSScriptRoot 'Run-Server.cmd')",
            preparationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Select-Object -First 1",
            runAllResolutionSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-Filter '*.App.exe'",
            runAllResolutionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$serverDlls.Count -ne 1",
            runAllResolutionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$appExecutables.Count -ne 1",
            runAllResolutionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "set_api_script_sha256",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "initial_appsettings_sha256",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "app_execution_tree_sha256",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "server_execution_tree_sha256",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-RuntimeExecutionTreeManifestDigest",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Set-AndVerify-IsolatedApiBaseUrl",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "The isolated app API base URL does not match the selected",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Standalone App launch is disabled for isolation safety.",
            preparationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$runExitCode = 1",
            runAllSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteTestRunScripts_InvalidatesExistingCertificationBeforeReplacingLaunchers()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "New-Utf8NoBomEncoding",
                "New-Utf8BomEncoding",
                "Write-Utf8File",
                "Set-RuntimeInvalidationMarker",
                "Write-TestRunScripts"
            ],
            """
            $runtimeRoot = Join-Path $PSScriptRoot 'certified-runtime'
            New-Item -ItemType Directory -Path $runtimeRoot -Force |
                Out-Null
            $readyMarker =
                Join-Path $runtimeRoot '.georaeplan-runtime-ready'
            $invalidMarker =
                Join-Path $runtimeRoot '.georaeplan-runtime-invalid'
            $runAllScript = Join-Path $runtimeRoot 'Run-All.ps1'
            [IO.File]::WriteAllText(
                $readyMarker,
                'runtime_ready=True')
            [IO.File]::WriteAllText(
                $runAllScript,
                'old certified launcher')

            Write-TestRunScripts `
                -OutputRoot $runtimeRoot `
                -DefaultBaseUrl 'http://127.0.0.1:19080' `
                -DotnetExe 'C:\Program Files\dotnet\dotnet.exe' `
                -CertificationId 'replacement-certification-id' `
                -CertificationMode 'replacement-test' `
                -PasswordResetCount 0

            if (Test-Path -LiteralPath $readyMarker) {
                throw 'Launcher replacement left the previous ready marker usable.'
            }
            if (-not (Test-Path -LiteralPath $invalidMarker -PathType Leaf)) {
                throw 'Launcher replacement did not publish an invalid marker.'
            }
            $invalidContent =
                Get-Content -LiteralPath $invalidMarker -Raw
            if ($invalidContent -notmatch 'reason=test-launcher-replacement') {
                throw 'Launcher replacement published the wrong invalidation reason.'
            }
            if (
                (Get-Content -LiteralPath $runAllScript -Raw) -eq
                    'old certified launcher'
            ) {
                throw 'Launcher replacement did not write the new Run-All script.'
            }

            Write-Output 'launcher-replacement-invalidated'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "launcher-replacement-invalidated",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteTestRunScripts_PublishesInvalidMarkerBeforeFirstLauncherWrite()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "New-Utf8NoBomEncoding",
                "New-Utf8BomEncoding",
                "Write-Utf8File",
                "Set-RuntimeInvalidationMarker",
                "Write-TestRunScripts"
            ],
            """
            $runtimeRoot = Join-Path $PSScriptRoot 'locked-launcher-runtime'
            New-Item -ItemType Directory -Path $runtimeRoot -Force |
                Out-Null
            $firstLauncher = Join-Path $runtimeRoot 'Run-App.cmd'
            $invalidMarker =
                Join-Path $runtimeRoot '.georaeplan-runtime-invalid'
            [IO.File]::WriteAllText($firstLauncher, 'old launcher')

            $launcherLock = [IO.File]::Open(
                $firstLauncher,
                [IO.FileMode]::Open,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
            try {
                $writeFailed = $false
                try {
                    Write-TestRunScripts `
                        -OutputRoot $runtimeRoot `
                        -DefaultBaseUrl 'http://127.0.0.1:19080' `
                        -DotnetExe 'C:\Program Files\dotnet\dotnet.exe' `
                        -CertificationId 'locked-launcher-certification-id' `
                        -CertificationMode 'locked-launcher-test' `
                        -PasswordResetCount 0
                }
                catch {
                    $writeFailed = $true
                }

                if (-not $writeFailed) {
                    throw 'The locked first launcher did not stop replacement.'
                }
                if (-not (
                    Test-Path -LiteralPath $invalidMarker -PathType Leaf
                )) {
                    throw (
                        'The runtime was not invalidated before the first ' +
                        'launcher write failed.')
                }
                $invalidContent =
                    Get-Content -LiteralPath $invalidMarker -Raw
                if (
                    $invalidContent -notmatch
                        'reason=test-launcher-replacement'
                ) {
                    throw 'The failed replacement used the wrong invalidation reason.'
                }
            }
            finally {
                $launcherLock.Dispose()
            }

            if (
                (Get-Content -LiteralPath $firstLauncher -Raw) -ne
                    'old launcher'
            ) {
                throw 'The locked launcher changed despite the failed replacement.'
            }
            if (Test-Path -LiteralPath (Join-Path $runtimeRoot 'Run-Server.cmd')) {
                throw 'Launcher replacement continued after the first write failed.'
            }

            Write-Output 'launcher-invalidated-before-first-write'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "launcher-invalidated-before-first-write",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedComponentLauncher_RejectsMissingReadyMarkerAndPreparationLease()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "New-Utf8NoBomEncoding",
                "New-Utf8BomEncoding",
                "Write-Utf8File",
                "Set-RuntimeInvalidationMarker",
                "Write-TestRunScripts"
            ],
            """
            $runtimeRoot = Join-Path $PSScriptRoot 'generated-runtime'
            New-Item -ItemType Directory -Path $runtimeRoot -Force |
                Out-Null
            Write-TestRunScripts `
                -OutputRoot $runtimeRoot `
                -DefaultBaseUrl 'http://127.0.0.1:19080' `
                -DotnetExe 'C:\Program Files\dotnet\dotnet.exe' `
                -CertificationId 'fixture-certification-id' `
                -CertificationMode 'fixture-mode' `
                -PasswordResetCount 0

            $componentScript =
                Join-Path $runtimeRoot 'Run-IsolatedComponent.ps1'
            foreach ($generatedScript in @(
                $componentScript,
                (Join-Path $runtimeRoot 'Run-All.ps1')
            )) {
                $tokens = $null
                $parseErrors = $null
                [Management.Automation.Language.Parser]::ParseFile(
                    $generatedScript,
                    [ref]$tokens,
                    [ref]$parseErrors) |
                    Out-Null
                if ($parseErrors.Count -ne 0) {
                    throw (
                        'Generated launcher parse failure: ' +
                        (($parseErrors |
                            ForEach-Object Message) -join '; '))
                }
                if (
                    (Get-Content `
                        -LiteralPath $generatedScript `
                        -Raw) -match 'LeaseProbeMilliseconds'
                ) {
                    throw 'A production launcher exposed the internal lock probe.'
                }
            }
            $generatedInvalidMarker =
                Join-Path $runtimeRoot '.georaeplan-runtime-invalid'
            Remove-Item `
                -LiteralPath $generatedInvalidMarker `
                -Force
            $missingMarkerOutput =
                Join-Path $runtimeRoot 'missing-marker.output'
            $savedErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & powershell `
                    -NoProfile `
                    -WindowStyle Hidden `
                    -ExecutionPolicy Bypass `
                    -File $componentScript `
                    -Mode Server *>$missingMarkerOutput
                $missingMarkerExitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $savedErrorActionPreference
            }
            if ($missingMarkerExitCode -eq 0) {
                throw 'The component launcher accepted a missing ready marker.'
            }
            $missingMarkerContent =
                Get-Content -LiteralPath $missingMarkerOutput -Raw
            if ($missingMarkerContent -notmatch 'not certified ready') {
                throw (
                    'The component launcher did not use the missing-ready ' +
                    "gate. output=$missingMarkerContent")
            }

            $readyMarker =
                Join-Path $runtimeRoot '.georaeplan-runtime-ready'
            [IO.File]::WriteAllText($readyMarker, 'runtime_ready=True')
            $invalidMarker =
                Join-Path $runtimeRoot '.georaeplan-runtime-invalid'
            [IO.File]::WriteAllText(
                $invalidMarker,
                'reason=fixture-invalid-marker')

            function Assert-BatchLauncherFails {
                param(
                    [string]$LauncherName,
                    [string]$ExpectedOutputPattern
                )

                $launcherPath = Join-Path $runtimeRoot $LauncherName
                $startInfo = [Diagnostics.ProcessStartInfo]::new()
                $startInfo.FileName = $env:ComSpec
                $startInfo.Arguments = (
                    '/d /c ""' +
                    $launcherPath.Replace('"', '\"') +
                    '" <nul"')
                $startInfo.UseShellExecute = $false
                $startInfo.CreateNoWindow = $true
                $startInfo.RedirectStandardOutput = $true
                $startInfo.RedirectStandardError = $true
                $process = [Diagnostics.Process]::Start($startInfo)
                try {
                    if (-not $process.WaitForExit(10000)) {
                        $process.Kill()
                        throw "$LauncherName timed out."
                    }
                    $process.Refresh()
                    $output =
                        $process.StandardOutput.ReadToEnd() +
                        $process.StandardError.ReadToEnd()
                    if ($process.ExitCode -eq 0) {
                        throw (
                            "$LauncherName accepted simultaneous ready " +
                            'and invalid markers.')
                    }
                    if ($output -notmatch $ExpectedOutputPattern) {
                        throw (
                            "$LauncherName returned the wrong failure. " +
                            "output=$output")
                    }
                }
                finally {
                    $process.Dispose()
                }
            }

            Assert-BatchLauncherFails `
                -LauncherName 'Run-All.cmd' `
                -ExpectedOutputPattern 'explicitly invalidated'
            Assert-BatchLauncherFails `
                -LauncherName 'Run-App.cmd' `
                -ExpectedOutputPattern 'explicitly invalidated'
            Assert-BatchLauncherFails `
                -LauncherName 'Run-Server.cmd' `
                -ExpectedOutputPattern 'explicitly invalidated'
            Remove-Item -LiteralPath $invalidMarker -Force

            $forgedMarkerError =
                Join-Path $runtimeRoot 'forged-marker.stderr'
            $ErrorActionPreference = 'Continue'
            try {
                & powershell `
                    -NoProfile `
                    -WindowStyle Hidden `
                    -ExecutionPolicy Bypass `
                    -File $componentScript `
                    -Mode Server 2>$forgedMarkerError |
                    Out-Null
                $forgedMarkerExitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference =
                    $savedErrorActionPreference
            }
            if (
                $forgedMarkerExitCode -eq 0
            ) {
                throw 'A forged ready marker was not rejected by certification.'
            }

            $leasePath =
                Join-Path $runtimeRoot '.georaeplan-prepare.lock'
            $preparationLease = [IO.File]::Open(
                $leasePath,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
            try {
                $ErrorActionPreference = 'Continue'
                try {
                    & powershell `
                        -NoProfile `
                        -WindowStyle Hidden `
                        -ExecutionPolicy Bypass `
                        -File $componentScript `
                        -Mode Server *>$null
                    $preparationLeaseExitCode = $LASTEXITCODE
                }
                finally {
                    $ErrorActionPreference =
                        $savedErrorActionPreference
                }
                if ($preparationLeaseExitCode -eq 0) {
                    throw 'The component launcher ignored the preparation lease.'
                }
            }
            finally {
                $preparationLease.Dispose()
            }

            Write-Output 'launcher-certification-gates'
            """);

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "launcher-certification-gates",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedLaunchers_EnforceComponentLockMatrix()
    {
        var result = await RunPreparationFunctionsAsync(
            [
                "New-Utf8NoBomEncoding",
                "New-Utf8BomEncoding",
                "Write-Utf8File",
                "Set-RuntimeInvalidationMarker",
                "Write-TestRunScripts",
                "Get-AppSnapshotFileManifest",
                "Get-AppSnapshotFileManifestDigest",
                "Get-RuntimeExecutionTreeManifestDigest"
            ],
            """
            $runtimeRoot = Join-Path $PSScriptRoot 'lock-matrix-runtime'
            $appRoot = Join-Path $runtimeRoot 'App'
            $serverRoot = Join-Path $runtimeRoot 'Server'
            New-Item -ItemType Directory -Path $appRoot -Force |
                Out-Null
            New-Item -ItemType Directory -Path $serverRoot -Force |
                Out-Null
            New-Item `
                -ItemType Directory `
                -Path (Join-Path $runtimeRoot 'ServerData') `
                -Force |
                Out-Null
            $appArtifact =
                Join-Path $appRoot 'fixture.Desktop.App.exe'
            $serverArtifact =
                Join-Path $serverRoot 'fixture.Server.Api.dll'
            $appDependency =
                Join-Path $appRoot 'fixture-app-dependency.dll'
            $serverDependency =
                Join-Path $serverRoot 'fixture-server-dependency.dll'
            [IO.File]::WriteAllText($appArtifact, 'fixture-app')
            [IO.File]::WriteAllText($serverArtifact, 'fixture-server')
            [IO.File]::WriteAllText(
                $appDependency,
                'fixture-app-dependency')
            [IO.File]::WriteAllText(
                $serverDependency,
                'fixture-server-dependency')
            $appSettings = Join-Path $appRoot 'appsettings.json'
            $initialAppSettingsContent =
                '{"Api":{"BaseUrl":"http://127.0.0.1:19080"}}'
            [IO.File]::WriteAllText(
                $appSettings,
                $initialAppSettingsContent)
            $setApiScript =
                Join-Path $runtimeRoot 'Set-ApiBaseUrl.ps1'
            $setApiScriptContent =
                "param([string]`$BaseUrl,[string[]]`$AppSettingsPaths)`n" +
                "foreach (`$path in `$AppSettingsPaths) {`n" +
                "  `$json = Get-Content -LiteralPath `$path -Raw | ConvertFrom-Json`n" +
                "  `$json.Api.BaseUrl = `$BaseUrl.Trim().TrimEnd('/')`n" +
                "  `$json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath `$path -Encoding UTF8`n" +
                "}"
            [IO.File]::WriteAllText(
                $setApiScript,
                $setApiScriptContent)
            $appDataRoot = Join-Path $runtimeRoot 'AppData'
            $appDataDirectory = Join-Path $appDataRoot 'data'
            New-Item `
                -ItemType Directory `
                -Path $appDataDirectory `
                -Force |
                Out-Null
            foreach ($volatileDirectory in @('logs', 'temp')) {
                $volatileRoot =
                    Join-Path $appDataRoot $volatileDirectory
                New-Item `
                    -ItemType Directory `
                    -Path $volatileRoot `
                    -Force |
                    Out-Null
                [IO.File]::WriteAllText(
                    (Join-Path $volatileRoot 'runtime-volatile.txt'),
                    "volatile:$volatileDirectory")
            }
            $databaseBaseName =
                ([string][char]0xAC70) +
                ([string][char]0xB798) +
                ([string][char]0xD50C) +
                ([string][char]0xB79C)
            $appDatabase =
                Join-Path $appDataDirectory "$databaseBaseName.db"
            $serverDatabase =
                Join-Path $serverRoot "$databaseBaseName-local.db"
            [IO.File]::WriteAllText($appDatabase, 'fixture-app-database')
            [IO.File]::WriteAllText(
                $serverDatabase,
                'fixture-server-database')
            $snapshotBackupDirectory =
                Join-Path $appDataRoot 'backup'
            New-Item `
                -ItemType Directory `
                -Path $snapshotBackupDirectory `
                -Force |
                Out-Null
            [IO.File]::WriteAllText(
                (Join-Path $snapshotBackupDirectory "$databaseBaseName.db"),
                'fixture-source-backup-database')
            $managedManifest = @(
                Get-AppSnapshotFileManifest -Root $appDataRoot
            )
            $managedDigest =
                Get-AppSnapshotFileManifestDigest `
                    -Manifest $managedManifest
            $serverExecutionExclusions = @(
                "$databaseBaseName-local.db",
                "$databaseBaseName-local.db-shm",
                "$databaseBaseName-local.db-wal",
                "$databaseBaseName-local.db-journal"
            )

            $fakeDotnet = Join-Path $runtimeRoot 'fake-dotnet.cmd'
            [IO.File]::WriteAllText(
                $fakeDotnet,
                "@echo off`r`nexit /b 23")
            Write-TestRunScripts `
                -OutputRoot $runtimeRoot `
                -DefaultBaseUrl 'http://127.0.0.1:19080' `
                -DotnetExe $fakeDotnet `
                -CertificationId 'lock-matrix-certification' `
                -CertificationMode 'lock-matrix-mode' `
                -PasswordResetCount 0 `
                -IncludeInternalLockProbe

            $componentScript =
                Join-Path $runtimeRoot 'Run-IsolatedComponent.ps1'
            $runAllScript = Join-Path $runtimeRoot 'Run-All.ps1'
            Remove-Item `
                -LiteralPath (
                    Join-Path `
                        $runtimeRoot `
                        '.georaeplan-runtime-invalid') `
                -Force
            $runAllSource =
                Get-Content -LiteralPath $runAllScript -Raw
            if ($runAllSource -notmatch '/readyz') {
                throw 'Run-All does not wait for database readiness.'
            }
            $markerPath =
                Join-Path $runtimeRoot '.georaeplan-runtime-ready'
            $marker = @(
                'runtime_ready=True'
                'runtime_state=pristine'
                "runtime_root=$runtimeRoot"
                "runtime_physical_root=$([IO.Path]::GetFullPath($runtimeRoot))"
                'certification_id=lock-matrix-certification'
                'certification_mode=lock-matrix-mode'
                'password_reset_count=0'
                "certified_at_utc=$([DateTimeOffset]::UtcNow.ToString('O'))"
                "managed_file_manifest_sha256=$managedDigest"
                "isolated_app_database_sha256=$((Get-FileHash -LiteralPath $appDatabase -Algorithm SHA256).Hash)"
                "server_database_sha256=$((Get-FileHash -LiteralPath $serverDatabase -Algorithm SHA256).Hash)"
                "app_executable_sha256=$((Get-FileHash -LiteralPath $appArtifact -Algorithm SHA256).Hash)"
                "server_dll_sha256=$((Get-FileHash -LiteralPath $serverArtifact -Algorithm SHA256).Hash)"
                "app_execution_tree_sha256=$(Get-RuntimeExecutionTreeManifestDigest -Root $appRoot -ExcludedRelativePaths @('appsettings.json'))"
                "server_execution_tree_sha256=$(Get-RuntimeExecutionTreeManifestDigest -Root $serverRoot -ExcludedRelativePaths $serverExecutionExclusions)"
                "set_api_script_sha256=$((Get-FileHash -LiteralPath $setApiScript -Algorithm SHA256).Hash)"
                "initial_appsettings_sha256=$((Get-FileHash -LiteralPath $appSettings -Algorithm SHA256).Hash)"
                "component_script_sha256=$((Get-FileHash -LiteralPath $componentScript -Algorithm SHA256).Hash)"
                "run_all_script_sha256=$((Get-FileHash -LiteralPath $runAllScript -Algorithm SHA256).Hash)"
                'android_package_state=absent'
                'android_package_file_name=none'
                'android_package_sha256=none'
                'android_package_metadata_sha256=none'
            ) -join [Environment]::NewLine
            Write-Utf8File `
                -Path $markerPath `
                -Content $marker `
                -WithBom

            $script:probeProcesses =
                [Collections.Generic.List[Diagnostics.Process]]::new()
            function Start-LeaseProbe {
                param(
                    [string]$ScriptPath,
                    [string[]]$Arguments,
                    [string]$Tag
                )

                $startInfo = [Diagnostics.ProcessStartInfo]::new()
                $startInfo.FileName = 'powershell.exe'
                $startInfo.Arguments = (
                    '-NoProfile -ExecutionPolicy Bypass -File "' +
                    $ScriptPath.Replace('"', '\"') +
                    '" ' +
                    (@($Arguments) -join ' '))
                $startInfo.UseShellExecute = $false
                $startInfo.CreateNoWindow = $true
                $startInfo.RedirectStandardOutput = $true
                $startInfo.RedirectStandardError = $true
                $process = [Diagnostics.Process]::Start($startInfo)
                $script:probeProcesses.Add($process)
                return [pscustomobject]@{
                    Process = $process
                }
            }

            function Wait-ProbeExit {
                param(
                    [object]$Probe,
                    [bool]$ExpectSuccess,
                    [string]$Context,
                    [string]$ExpectedErrorPattern = ''
                )

                if (-not $Probe.Process.WaitForExit(12000)) {
                    $Probe.Process.Kill()
                    throw "$Context timed out."
                }
                $Probe.Process.Refresh()
                $stdout =
                    $Probe.Process.StandardOutput.ReadToEnd()
                $stderr =
                    $Probe.Process.StandardError.ReadToEnd()
                $succeeded = $Probe.Process.ExitCode -eq 0
                if ($succeeded -ne $ExpectSuccess) {
                    throw (
                        "$Context exit=$($Probe.Process.ExitCode) " +
                        "stderr=$stderr")
                }
                if (
                    -not $ExpectSuccess -and
                    -not [string]::IsNullOrWhiteSpace(
                        $ExpectedErrorPattern) -and
                    $stderr -notmatch $ExpectedErrorPattern
                ) {
                    throw (
                        "$Context returned the wrong error. " +
                        "stderr=$stderr stdout=$stdout")
                }
            }

            function Assert-MarkerPristine {
                if (
                    (Get-Content -LiteralPath $markerPath -Raw) -notmatch
                        'runtime_state=pristine'
                ) {
                    throw 'A rejected prelaunch check consumed pristine state.'
                }
            }

            function Set-MarkerValue {
                param(
                    [string]$Key,
                    [string]$Value
                )

                $found = $false
                $lines = @(
                    Get-Content -LiteralPath $markerPath |
                        ForEach-Object {
                            if ($_.StartsWith(
                                    "$Key=",
                                    [StringComparison]::OrdinalIgnoreCase)) {
                                $found = $true
                                "$Key=$Value"
                            }
                            else {
                                $_
                            }
                        }
                )
                if (-not $found) {
                    throw "Marker key was not found: $Key"
                }
                [IO.File]::WriteAllLines(
                    $markerPath,
                    $lines,
                    [Text.UTF8Encoding]::new($true))
            }

            function Wait-LockHeld {
                param(
                    [string]$Path,
                    [object]$Probe
                )

                for ($attempt = 0; $attempt -lt 100; $attempt++) {
                    if ($Probe.Process.HasExited) {
                        $stderr =
                            $Probe.Process.StandardError.ReadToEnd()
                        throw (
                            'Lease holder exited before acquiring its lock. ' +
                            "exit=$($Probe.Process.ExitCode) stderr=$stderr")
                    }
                    $probeLease = $null
                    try {
                        $probeLease = [IO.File]::Open(
                            $Path,
                            [IO.FileMode]::OpenOrCreate,
                            [IO.FileAccess]::ReadWrite,
                            [IO.FileShare]::None)
                    }
                    catch {
                        return
                    }
                    finally {
                        if ($null -ne $probeLease) {
                            $probeLease.Dispose()
                        }
                    }
                    Start-Sleep -Milliseconds 50
                }
                throw "Expected lock was not held: $Path"
            }

            $appLock =
                Join-Path $runtimeRoot '.georaeplan-runtime-app.lock'
            $serverLock =
                Join-Path $runtimeRoot '.georaeplan-runtime-server.lock'
            try {
                $setApiScriptBackup =
                    Join-Path $runtimeRoot 'Set-ApiBaseUrl.missing-test.ps1'
                Move-Item `
                    -LiteralPath $setApiScript `
                    -Destination $setApiScriptBackup
                $missingSetApiComponentProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App'
                    ) `
                    -Tag 'missing-set-api-component'
                Wait-ProbeExit `
                    -Probe $missingSetApiComponentProbe `
                    -ExpectSuccess $false `
                    -Context 'component missing Set-ApiBaseUrl rejection' `
                    -ExpectedErrorPattern 'required runtime path is missing'
                Assert-MarkerPristine
                $legacyRunAllMutexCreatedNew = $false
                $legacyRunAllMutex = New-Object `
                    System.Threading.Mutex(
                        $true,
                        'Local\GeoraePlan_Test_RunAll_Launcher',
                        [ref]$legacyRunAllMutexCreatedNew)
                try {
                    $missingSetApiRunAllProbe = Start-LeaseProbe `
                        -ScriptPath $runAllScript `
                        -Arguments @() `
                        -Tag 'missing-set-api-runall'
                    Wait-ProbeExit `
                        -Probe $missingSetApiRunAllProbe `
                        -ExpectSuccess $false `
                        -Context 'Run-All missing Set-ApiBaseUrl rejection' `
                        -ExpectedErrorPattern 'required runtime path is missing'
                }
                finally {
                    if ($legacyRunAllMutexCreatedNew) {
                        $legacyRunAllMutex.ReleaseMutex()
                    }
                    $legacyRunAllMutex.Dispose()
                }
                Assert-MarkerPristine
                Move-Item `
                    -LiteralPath $setApiScriptBackup `
                    -Destination $setApiScript

                [IO.File]::AppendAllText(
                    $setApiScript,
                    'tampered-set-api-script')
                $tamperedSetApiProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App'
                    ) `
                    -Tag 'tampered-set-api'
                Wait-ProbeExit `
                    -Probe $tamperedSetApiProbe `
                    -ExpectSuccess $false `
                    -Context 'Set-ApiBaseUrl tamper rejection' `
                    -ExpectedErrorPattern 'certified runtime artifact has changed'
                Assert-MarkerPristine
                [IO.File]::WriteAllText(
                    $setApiScript,
                    $setApiScriptContent)

                [IO.File]::WriteAllText(
                    $appSettings,
                    '{"Api":{"BaseUrl":"http://127.0.0.1:19999"}}')
                $tamperedAppSettingsProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App'
                    ) `
                    -Tag 'tampered-appsettings'
                Wait-ProbeExit `
                    -Probe $tamperedAppSettingsProbe `
                    -ExpectSuccess $false `
                    -Context 'initial appsettings tamper rejection' `
                    -ExpectedErrorPattern 'Pristine runtime data has changed'
                Assert-MarkerPristine
                [IO.File]::WriteAllText(
                    $appSettings,
                    $initialAppSettingsContent)

                $appDatabaseWal = "$appDatabase-wal"
                [IO.File]::WriteAllText(
                    $appDatabaseWal,
                    'uncertified-wal')
                $sidecarProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App'
                    ) `
                    -Tag 'uncertified-sidecar'
                Wait-ProbeExit `
                    -Probe $sidecarProbe `
                    -ExpectSuccess $false `
                    -Context 'pristine SQLite sidecar rejection' `
                    -ExpectedErrorPattern 'uncertified SQLite sidecar'
                Assert-MarkerPristine
                Remove-Item -LiteralPath $appDatabaseWal -Force

                $appRootOriginal =
                    Join-Path $runtimeRoot 'App-original'
                Move-Item `
                    -LiteralPath $appRoot `
                    -Destination $appRootOriginal
                try {
                    New-Item `
                        -ItemType Junction `
                        -Path $appRoot `
                        -Target $appRootOriginal |
                        Out-Null
                    $appJunctionProbe = Start-LeaseProbe `
                        -ScriptPath $componentScript `
                        -Arguments @(
                            '-Mode',
                            'App'
                        ) `
                        -Tag 'app-junction'
                    Wait-ProbeExit `
                        -Probe $appJunctionProbe `
                        -ExpectSuccess $false `
                        -Context 'App junction rejection' `
                        -ExpectedErrorPattern 'reparse point'
                    Assert-MarkerPristine
                }
                finally {
                    if (Test-Path -LiteralPath $appRoot) {
                        [IO.Directory]::Delete($appRoot)
                    }
                    if (
                        (Test-Path -LiteralPath $appRootOriginal) -and
                        -not (Test-Path -LiteralPath $appRoot)
                    ) {
                        Move-Item `
                            -LiteralPath $appRootOriginal `
                            -Destination $appRoot
                    }
                }

                $serverDatabaseHardLink =
                    Join-Path $serverRoot 'server-database-hardlink.db'
                New-Item `
                    -ItemType HardLink `
                    -Path $serverDatabaseHardLink `
                    -Target $serverDatabase |
                    Out-Null
                $hardLinkProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App'
                    ) `
                    -Tag 'database-hardlink'
                Wait-ProbeExit `
                    -Probe $hardLinkProbe `
                    -ExpectSuccess $false `
                    -Context 'database hard-link rejection' `
                    -ExpectedErrorPattern 'multiple hard links'
                Assert-MarkerPristine
                Remove-Item `
                    -LiteralPath $serverDatabaseHardLink `
                    -Force

                $junctionTarget =
                    Join-Path $runtimeRoot 'junction-target'
                New-Item `
                    -ItemType Directory `
                    -Path $junctionTarget `
                    -Force |
                    Out-Null
                $managedJunction =
                    Join-Path $appDataRoot 'managed-junction'
                New-Item `
                    -ItemType Junction `
                    -Path $managedJunction `
                    -Target $junctionTarget |
                    Out-Null
                $junctionProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App'
                    ) `
                    -Tag 'managed-junction'
                Wait-ProbeExit `
                    -Probe $junctionProbe `
                    -ExpectSuccess $false `
                    -Context 'managed junction rejection' `
                    -ExpectedErrorPattern 'reparse point'
                Assert-MarkerPristine
                Remove-Item `
                    -LiteralPath $managedJunction `
                    -Force

                [IO.File]::AppendAllText(
                    $serverDatabase,
                    'tampered-before-first-launch')
                $pristineDataTamperProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App'
                    ) `
                    -Tag 'pristine-data-tamper'
                Wait-ProbeExit `
                    -Probe $pristineDataTamperProbe `
                    -ExpectSuccess $false `
                    -Context 'pristine data tamper rejection' `
                    -ExpectedErrorPattern 'Pristine runtime data has changed'
                Assert-MarkerPristine
                [IO.File]::WriteAllText(
                    $serverDatabase,
                    'fixture-server-database')

                $appHolder = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App',
                        '-LeaseProbeMilliseconds',
                        '6000'
                    ) `
                    -Tag 'app-holder'
                Wait-LockHeld -Path $appLock -Probe $appHolder

                $serverAlongsideApp = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'Server',
                        '-LeaseProbeMilliseconds',
                        '500'
                    ) `
                    -Tag 'server-alongside-app'
                Wait-ProbeExit `
                    -Probe $serverAlongsideApp `
                    -ExpectSuccess $true `
                    -Context 'App and Server coexistence'

                $duplicateApp = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App',
                        '-LeaseProbeMilliseconds',
                        '100'
                    ) `
                    -Tag 'duplicate-app'
                Wait-ProbeExit `
                    -Probe $duplicateApp `
                    -ExpectSuccess $false `
                    -Context 'duplicate App rejection' `
                    -ExpectedErrorPattern 'Another isolated App component'

                $runAllAgainstApp = Start-LeaseProbe `
                    -ScriptPath $runAllScript `
                    -Arguments @(
                        '-LeaseProbeMilliseconds',
                        '100'
                    ) `
                    -Tag 'runall-against-app'
                Wait-ProbeExit `
                    -Probe $runAllAgainstApp `
                    -ExpectSuccess $false `
                    -Context 'Run-All versus App rejection' `
                    -ExpectedErrorPattern 'isolated App or Server component'
                Wait-ProbeExit `
                    -Probe $appHolder `
                    -ExpectSuccess $true `
                    -Context 'App holder completion'

                $serverHolder = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'Server',
                        '-LeaseProbeMilliseconds',
                        '2500'
                    ) `
                    -Tag 'server-holder'
                Wait-LockHeld -Path $serverLock -Probe $serverHolder
                $duplicateServer = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'Server',
                        '-LeaseProbeMilliseconds',
                        '100'
                    ) `
                    -Tag 'duplicate-server'
                Wait-ProbeExit `
                    -Probe $duplicateServer `
                    -ExpectSuccess $false `
                    -Context 'duplicate Server rejection' `
                    -ExpectedErrorPattern 'Another isolated Server component'
                Wait-ProbeExit `
                    -Probe $serverHolder `
                    -ExpectSuccess $true `
                    -Context 'Server holder completion'

                $runAllHolder = Start-LeaseProbe `
                    -ScriptPath $runAllScript `
                    -Arguments @(
                        '-LeaseProbeMilliseconds',
                        '3500'
                    ) `
                    -Tag 'runall-holder'
                Wait-LockHeld -Path $appLock -Probe $runAllHolder
                Wait-LockHeld -Path $serverLock -Probe $runAllHolder
                foreach ($mode in @('App', 'Server')) {
                    $componentAgainstRunAll = Start-LeaseProbe `
                        -ScriptPath $componentScript `
                        -Arguments @(
                            '-Mode',
                            $mode,
                            '-LeaseProbeMilliseconds',
                            '100'
                        ) `
                        -Tag "component-against-runall-$mode"
                    Wait-ProbeExit `
                        -Probe $componentAgainstRunAll `
                        -ExpectSuccess $false `
                        -Context "$mode versus Run-All rejection" `
                        -ExpectedErrorPattern "Another isolated $mode component"
                }
                Wait-ProbeExit `
                    -Probe $runAllHolder `
                    -ExpectSuccess $true `
                    -Context 'Run-All holder completion'
                Assert-MarkerPristine

                $certificationHandoff = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App'
                    ) `
                    -Tag 'certification-handoff'
                Wait-ProbeExit `
                    -Probe $certificationHandoff `
                    -ExpectSuccess $false `
                    -Context 'fixture executable handoff'
                if (
                    (Get-Content -LiteralPath $markerPath -Raw) -notmatch
                        'runtime_state=mutable'
                ) {
                    throw 'Successful pristine validation did not become mutable.'
                }

                $port19080Guard = $null
                try {
                    $port19080Guard =
                        [Net.Sockets.TcpListener]::new(
                            [Net.IPAddress]::Loopback,
                            19080)
                    $port19080Guard.Start()
                }
                catch {
                    if ($null -ne $port19080Guard) {
                        $port19080Guard.Stop()
                        $port19080Guard = $null
                    }
                }
                try {
                    $occupiedPortServerProbe = Start-LeaseProbe `
                        -ScriptPath $componentScript `
                        -Arguments @(
                            '-Mode',
                            'Server'
                        ) `
                        -Tag 'occupied-port-server'
                    Wait-ProbeExit `
                        -Probe $occupiedPortServerProbe `
                        -ExpectSuccess $false `
                        -Context 'occupied 19080 server configuration'
                    $configuredSettings =
                        Get-Content -LiteralPath $appSettings -Raw |
                            ConvertFrom-Json
                    $configuredBaseUrl =
                        [string]$configuredSettings.Api.BaseUrl
                    if (
                        $configuredBaseUrl -eq
                            'http://127.0.0.1:19080' -or
                        $configuredBaseUrl -notmatch
                            '^http://127\.0\.0\.1:\d+$'
                    ) {
                        throw (
                            'The isolated server did not publish its selected ' +
                            "non-19080 URL. actual=$configuredBaseUrl")
                    }
                }
                finally {
                    if ($null -ne $port19080Guard) {
                        $port19080Guard.Stop()
                    }
                }

                $exitSetterContent =
                    "param([string]`$BaseUrl,[string[]]`$AppSettingsPaths)`n" +
                    'exit 37'
                [IO.File]::WriteAllText(
                    $setApiScript,
                    $exitSetterContent)
                Set-MarkerValue `
                    -Key 'set_api_script_sha256' `
                    -Value (
                        Get-FileHash `
                            -LiteralPath $setApiScript `
                            -Algorithm SHA256
                    ).Hash
                $exitSetterProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'Server'
                    ) `
                    -Tag 'exit-setter'
                Wait-ProbeExit `
                    -Probe $exitSetterProbe `
                    -ExpectSuccess $false `
                    -Context 'nonzero Set-ApiBaseUrl rejection' `
                    -ExpectedErrorPattern 'Failed to update the isolated app API base URL'

                $noOpSetterContent =
                    "param([string]`$BaseUrl,[string[]]`$AppSettingsPaths)`n" +
                    'exit 0'
                [IO.File]::WriteAllText(
                    $appSettings,
                    '{"Api":{"BaseUrl":"http://127.0.0.1:19999"}}')
                [IO.File]::WriteAllText(
                    $setApiScript,
                    $noOpSetterContent)
                Set-MarkerValue `
                    -Key 'set_api_script_sha256' `
                    -Value (
                        Get-FileHash `
                            -LiteralPath $setApiScript `
                            -Algorithm SHA256
                    ).Hash
                $noOpSetterProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'Server'
                    ) `
                    -Tag 'noop-setter'
                Wait-ProbeExit `
                    -Probe $noOpSetterProbe `
                    -ExpectSuccess $false `
                    -Context 'no-op Set-ApiBaseUrl rejection' `
                    -ExpectedErrorPattern 'does not match the selected server URL'

                [IO.File]::WriteAllText(
                    $setApiScript,
                    $setApiScriptContent)
                Set-MarkerValue `
                    -Key 'set_api_script_sha256' `
                    -Value (
                        Get-FileHash `
                            -LiteralPath $setApiScript `
                            -Algorithm SHA256
                    ).Hash

                [IO.File]::AppendAllText(
                    $serverDependency,
                    'tampered')
                $tamperedDependencyProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'Server'
                    ) `
                    -Tag 'tampered-server-dependency'
                Wait-ProbeExit `
                    -Probe $tamperedDependencyProbe `
                    -ExpectSuccess $false `
                    -Context 'dependent DLL tamper rejection' `
                    -ExpectedErrorPattern 'A certified runtime artifact has changed'
                [IO.File]::WriteAllText(
                    $serverDependency,
                    'fixture-server-dependency')

                [IO.File]::AppendAllText(
                    $appArtifact,
                    'tampered')
                $tamperedArtifactProbe = Start-LeaseProbe `
                    -ScriptPath $componentScript `
                    -Arguments @(
                        '-Mode',
                        'App'
                    ) `
                    -Tag 'tampered-artifact'
                Wait-ProbeExit `
                    -Probe $tamperedArtifactProbe `
                    -ExpectSuccess $false `
                    -Context 'certified artifact tamper rejection' `
                    -ExpectedErrorPattern 'A certified runtime artifact has changed'
            }
            finally {
                foreach ($process in $script:probeProcesses) {
                    if (-not $process.HasExited) {
                        $process.Kill()
                        $process.WaitForExit()
                    }
                    $process.Dispose()
                }
            }

            Write-Output 'component-lock-matrix-verified'
            """,
            timeout: TimeSpan.FromSeconds(60));

        Assert.True(result.ExitCode == 0, BuildFailureMessage(result));
        Assert.Contains(
            "component-lock-matrix-verified",
            result.Stdout,
            StringComparison.Ordinal);
    }

    private static async Task<PowerShellResult> RunPreparationFunctionsAsync(
        IReadOnlyList<string> functionNames,
        string invocation,
        TimeSpan? timeout = null)
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"isolated-user-bootstrap-{Guid.NewGuid():N}");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);
        Directory.CreateDirectory(testRoot);
        var harnessPath = Path.Combine(testRoot, "harness.ps1");

        try
        {
            var quotedFunctionNames = string.Join(
                ", ",
                functionNames.Select(
                    name => "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'"));
            var harness =
                $$"""
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript
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

                foreach ($functionName in @({{quotedFunctionNames}})) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "Function was not found: $functionName"
                    }
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                {{invocation}}
                """;
            await File.WriteAllTextAsync(
                harnessPath,
                harness,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            return await RunPowerShellAsync(
                harnessPath,
                timeout ?? TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolvePreparationScript());
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
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

    private static string BuildFailureMessage(PowerShellResult result)
        =>
            "The PowerShell probe failed." +
            Environment.NewLine +
            "STDOUT:" + Environment.NewLine +
            result.Stdout +
            Environment.NewLine +
            "STDERR:" + Environment.NewLine +
            result.Stderr;

    private static string ResolvePreparationScript()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "테스트 시행",
            "테스트-환경-준비.ps1");
        Assert.True(File.Exists(path), $"Preparation script not found: {path}");
        return path;
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
