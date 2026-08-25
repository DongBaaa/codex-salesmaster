using System.Text.Json;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32.SafeHandles;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();
var canonicalizationCommitted = false;
const string usage = "usage: SyncDiag <prepare-test-seed|inspect-legacy-invoice-test-seed-profile|inspect-read-only-legacy-invoice-seed-profile <database-path>|preview-legacy-invoice-test-seed|canonicalize-legacy-invoice-test-seed|prepare-test-seed-retry|preseed-sync|mark-all-dirty|sync|maintenance-sync|inspect|stored-credential-envelopes|source-credential-envelopes|read-only-summary <database-path>|read-only-integrity-report <database-path> <tenant-code> <office-code> [--include-details]|snapshot-sqlite <source-db> <target-db>|finalize-test-app-sqlite|finalize-test-server-sqlite <database-path>>";
if (string.IsNullOrWhiteSpace(command))
{
    Console.Error.WriteLine(usage);
    return 2;
}

if (string.Equals(command, "--help", StringComparison.Ordinal) ||
    string.Equals(command, "-h", StringComparison.Ordinal) ||
    string.Equals(command, "help", StringComparison.Ordinal))
{
    Console.WriteLine(usage);
    return 0;
}

if (string.Equals(command, "read-only-summary", StringComparison.Ordinal))
{
    if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
    {
        Console.Error.WriteLine("usage: SyncDiag read-only-summary <database-path>");
        return 2;
    }

    return PrintReadOnlyDatabaseSummary(args[1]);
}

if (string.Equals(
        command,
        "read-only-integrity-report",
        StringComparison.Ordinal))
{
    if ((args.Length != 4 && args.Length != 5) ||
        string.IsNullOrWhiteSpace(args[1]) ||
        string.IsNullOrWhiteSpace(args[2]) ||
        string.IsNullOrWhiteSpace(args[3]) ||
        (args.Length == 5 &&
         !string.Equals(
             args[4],
             "--include-details",
             StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine(
            "usage: SyncDiag read-only-integrity-report <database-path> <tenant-code> <office-code> [--include-details]");
        return 2;
    }

    return await PrintReadOnlyIntegrityReportAsync(
        args[1],
        args[2],
        args[3],
        includeDetails: args.Length == 5);
}

if (string.Equals(
        command,
        "inspect-read-only-legacy-invoice-seed-profile",
        StringComparison.Ordinal))
{
    if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
    {
        Console.Error.WriteLine(
            "usage: SyncDiag inspect-read-only-legacy-invoice-seed-profile <database-path>");
        return 2;
    }

    return await PrintReadOnlyLegacyInvoiceSeedProfileAsync(args[1]);
}

try
{
    if (string.Equals(command, "snapshot-sqlite", StringComparison.Ordinal))
    {
        if (args.Length != 3 ||
            string.IsNullOrWhiteSpace(args[1]) ||
            string.IsNullOrWhiteSpace(args[2]))
        {
            Console.Error.WriteLine(
                "usage: SyncDiag snapshot-sqlite <source-db> <target-db>");
            return 2;
        }

        var snapshot = CreateStandaloneSqliteSnapshot(args[1], args[2]);
        Console.WriteLine("snapshot_succeeded=True");
        Console.WriteLine($"target_length={snapshot.TargetLength}");
        Console.WriteLine($"target_sha256={snapshot.TargetSha256}");
        Console.WriteLine($"quick_check={snapshot.QuickCheck}");
        Console.WriteLine($"sidecar_count={snapshot.SidecarCount}");
        return 0;
    }

    if (string.Equals(
            command,
            "finalize-test-server-sqlite",
            StringComparison.Ordinal))
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine(
                "usage: SyncDiag finalize-test-server-sqlite <database-path>");
            return 2;
        }

        var serverRoot = Environment.GetEnvironmentVariable(
            "GEORAEPLAN_TEST_SERVER_ROOT");
        if (string.IsNullOrWhiteSpace(serverRoot))
        {
            throw new InvalidOperationException(
                "Server SQLite finalization requires an explicit GEORAEPLAN_TEST_SERVER_ROOT.");
        }

        using var preparationLease =
            IsolatedPreparationDatabaseLease.AcquireForServerRoot(serverRoot);
        var finalization =
            IsolatedTestServerSqliteFinalizer.FinalizeDatabase(args[1]);
        preparationLease.AssertStable();
        Console.WriteLine("server_sqlite_finalized=True");
        Console.WriteLine($"database_path={finalization.DatabasePath}");
        Console.WriteLine($"database_length={finalization.DatabaseLength}");
        Console.WriteLine($"database_sha256={finalization.DatabaseSha256}");
        Console.WriteLine($"checkpoint_busy={finalization.CheckpointBusy}");
        Console.WriteLine(
            $"checkpoint_log_frames={finalization.CheckpointLogFrames}");
        Console.WriteLine(
            $"checkpointed_frames={finalization.CheckpointedFrames}");
        Console.WriteLine($"journal_mode={finalization.JournalMode}");
        Console.WriteLine($"quick_check={finalization.QuickCheck}");
        Console.WriteLine($"sidecar_count={finalization.SidecarCount}");
        return 0;
    }

    if (string.Equals(
            command,
            "finalize-test-app-sqlite",
            StringComparison.Ordinal))
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine(
                "usage: SyncDiag finalize-test-app-sqlite");
            return 2;
        }

        AssertIsolatedTestSeedCommandEnvironment();
        using var appPreparationLease =
            IsolatedPreparationDatabaseLease.AcquireForAppData(
                Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!,
                Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_SEED_ROOT")!);
        var finalization =
            IsolatedTestServerSqliteFinalizer.FinalizeAppDatabase(
                appPreparationLease);
        appPreparationLease.AssertStable();
        Console.WriteLine("app_sqlite_finalized=True");
        Console.WriteLine($"database_path={finalization.DatabasePath}");
        Console.WriteLine($"database_length={finalization.DatabaseLength}");
        Console.WriteLine($"database_sha256={finalization.DatabaseSha256}");
        Console.WriteLine($"checkpoint_busy={finalization.CheckpointBusy}");
        Console.WriteLine(
            $"checkpoint_log_frames={finalization.CheckpointLogFrames}");
        Console.WriteLine(
            $"checkpointed_frames={finalization.CheckpointedFrames}");
        Console.WriteLine($"journal_mode={finalization.JournalMode}");
        Console.WriteLine($"quick_check={finalization.QuickCheck}");
        Console.WriteLine($"sidecar_count={finalization.SidecarCount}");
        return 0;
    }

    var requiresIsolatedDatabaseLease =
        RequiresIsolatedTestDatabaseLease(command);
    if (requiresIsolatedDatabaseLease)
        AssertIsolatedTestSeedCommandEnvironment();

    using var isolatedDatabaseLease = requiresIsolatedDatabaseLease
        ? IsolatedPreparationDatabaseLease.AcquireForAppData(
            Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!,
            Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_SEED_ROOT")!)
        : null;
    var isProfileInspection = string.Equals(
        command,
        "inspect-legacy-invoice-test-seed-profile",
        StringComparison.Ordinal);
    using var canonicalizationAuthorization = isProfileInspection
        ? IsolatedLegacyInvoiceSeedCanonicalizer
            .AcquireProfileInspectionAuthorization(
                isolatedDatabaseLease ??
                throw new InvalidOperationException(
                    "The isolated profile-inspection database lease was not acquired."))
        : string.Equals(
            command,
            "canonicalize-legacy-invoice-test-seed",
            StringComparison.Ordinal) ||
        string.Equals(
            command,
            "preview-legacy-invoice-test-seed",
            StringComparison.Ordinal)
        ? IsolatedLegacyInvoiceSeedCanonicalizer.AcquireAuthorization(
            isolatedDatabaseLease ??
            throw new InvalidOperationException(
                "The isolated canonicalization database lease was not acquired."))
        : null;
    using var isolatedServerTargetLease =
        RequiresIsolatedTestServerTargetGuard(command)
            ? AcquireIsolatedTestServerTargetGuard()
            : null;

    if (string.Equals(
            command,
            "stored-credential-envelopes",
            StringComparison.Ordinal))
    {
        await PrintStoredCredentialEnvelopesAsync(
            isolatedDatabaseLease?.DatabasePath
            ?? throw new InvalidOperationException(
                "The isolated credential database lease was not acquired."));
        isolatedDatabaseLease.AssertStable();
        return 0;
    }

    if (string.Equals(
            command,
            "source-credential-envelopes",
            StringComparison.Ordinal))
    {
        await PrintSourceCredentialEnvelopesAsync(
            isolatedDatabaseLease?.DatabasePath
            ?? throw new InvalidOperationException(
                "The isolated credential database lease was not acquired."));
        isolatedDatabaseLease.AssertStable();
        return 0;
    }

    if (string.Equals(command, "inspect", StringComparison.Ordinal))
    {
        var databasePath =
            isolatedDatabaseLease?.DatabasePath ?? AppPaths.LocalDbFile;
        isolatedDatabaseLease?.AssertStable();
        using var inspectionGuard =
            ImmutableSqliteInspectionGuard.Acquire(databasePath);
        var connectionString =
            BuildImmutableInspectionConnectionString(
                inspectionGuard.DatabasePath);
        var inspectionOptions =
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .Options;
        await using (var inspectionDb =
                     new LocalDbContext(inspectionOptions))
        {
            Console.WriteLine("inspection_mode=read_only");
            Console.WriteLine(
                "inspection_source=immutable_sidecar_free_database");
            await PrintDirtyInspectionAsync(inspectionDb);
        }

        inspectionGuard.AssertStableSidecarFree();
        isolatedDatabaseLease?.AssertStable();
        return 0;
    }

    await using var db = new LocalDbContext();
    await LocalDbInitializer.InitializeAsync(db);
    isolatedDatabaseLease?.AssertStable();

    switch (command)
    {
        case "prepare-test-seed":
            await PrepareTestSeedAsync(db);
            return 0;
        case "inspect-legacy-invoice-test-seed-profile":
            var inspectedProfile =
                await IsolatedLegacyInvoiceSeedCanonicalizer
                    .PreviewUnapprovedProfileAsync(
                        db,
                        isolatedDatabaseLease ??
                            throw new InvalidOperationException(
                                "The isolated profile-inspection database lease was not acquired."),
                        canonicalizationAuthorization ??
                            throw new InvalidOperationException(
                                "The isolated profile-inspection authorization was not acquired."));
            Console.WriteLine(
                "legacy_invoice_seed_profile_inspection_succeeded=True");
            Console.WriteLine(
                $"legacy_invoice_seed_profile_inspection_sha256={inspectedProfile.ComputeSha256()}");
            Console.WriteLine(
                $"legacy_invoice_seed_profile_inspection_json={inspectedProfile.ToDeterministicJson()}");
            Console.WriteLine(
                $"legacy_invoice_seed_scope={IsolatedLegacyInvoiceSeedCanonicalizer.ActiveOperationalSeedScope}");
            return 0;
        case "preview-legacy-invoice-test-seed":
            var profilePreview =
                await IsolatedLegacyInvoiceSeedCanonicalizer
                    .PreviewProfileAsync(
                        db,
                        isolatedDatabaseLease ??
                            throw new InvalidOperationException(
                                "The isolated profile-preview database lease was not acquired."),
                        canonicalizationAuthorization ??
                            throw new InvalidOperationException(
                                "The isolated profile-preview authorization was not acquired."));
            var profilePreviewJson = profilePreview.ToDeterministicJson();
            Console.WriteLine(
                "legacy_invoice_seed_profile_preview_succeeded=True");
            Console.WriteLine(
                $"legacy_invoice_seed_profile_preview_sha256={profilePreview.ComputeSha256()}");
            Console.WriteLine(
                $"legacy_invoice_seed_profile_preview_json={profilePreviewJson}");
            Console.WriteLine(
                $"legacy_invoice_seed_scope={IsolatedLegacyInvoiceSeedCanonicalizer.ActiveOperationalSeedScope}");
            return 0;
        case "canonicalize-legacy-invoice-test-seed":
            try
            {
                var canonicalizationResult =
                    await IsolatedLegacyInvoiceSeedCanonicalizer
                        .CanonicalizeAsync(
                            db,
                            isolatedDatabaseLease ??
                                throw new InvalidOperationException(
                                "The isolated canonicalization database lease was not acquired."),
                            canonicalizationAuthorization ??
                                throw new InvalidOperationException(
                                    "The isolated canonicalization authorization was not acquired."));
                canonicalizationCommitted = true;
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .WriteAdvisoryCommandOutput(
                        Console.Out,
                        canonicalizationResult);
                return 0;
            }
            catch (Exception ex)
            {
                if (canonicalizationCommitted)
                    return 0;
                Console.Error.WriteLine(
                    BuildSanitizedCanonicalizationError(ex));
                return 1;
            }
        case "prepare-test-seed-retry":
            isolatedServerTargetLease?.AssertStable();
            var retryPreparation = await PrepareTestSeedRetryAsync(db);
            isolatedServerTargetLease?.AssertStable();
            Console.WriteLine(
                $"rebased_invoices={retryPreparation.RebasedInvoices}");
            Console.WriteLine($"rebased_payments={retryPreparation.RebasedPayments}");
            Console.WriteLine($"rebased_transactions={retryPreparation.RebasedTransactions}");
            Console.WriteLine(
                $"unlinked_excluded_rental_assets={retryPreparation.UnlinkedExcludedRentalAssets}");
            Console.WriteLine(
                $"closed_rental_assignment_histories={retryPreparation.ClosedRentalAssignmentHistories}");
            Console.WriteLine(
                $"removed_collateral_failed_assignment_outbox={retryPreparation.RemovedCollateralFailedAssignmentOutbox}");
            Console.WriteLine(
                $"removed_superseded_sent_assignment_outbox={retryPreparation.RemovedSupersededSentAssignmentOutbox}");
            Console.WriteLine($"removed_stale_outbox={retryPreparation.RemovedStaleOutbox}");
            Console.WriteLine(
                $"removed_clean_outbox={retryPreparation.RemovedCleanOutbox}");
            return 0;
        case "preseed-sync":
            isolatedServerTargetLease?.AssertStable();
            Console.WriteLine("sync_ok=True");
            return 0;
        case "mark-all-dirty":
            var markedCount = await MarkAllDirtyAsync(db);
            Console.WriteLine($"marked_dirty={markedCount}");
            return 0;
        case "sync":
            var syncExitCode = await RunSyncAsync(db);
            isolatedServerTargetLease?.AssertStable();
            return syncExitCode;
        case "maintenance-sync":
            var maintenanceSyncExitCode =
                await RunSyncAsync(db);
            isolatedServerTargetLease?.AssertStable();
            return maintenanceSyncExitCode;
        default:
            Console.Error.WriteLine($"unknown command: {command}");
            return 2;
    }
}
catch (Exception ex) when (
    string.Equals(
        command,
        "snapshot-sqlite",
        StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        BuildSanitizedSnapshotError(ex));
    return 1;
}
catch (Exception ex) when (
    string.Equals(
        command,
        "canonicalize-legacy-invoice-test-seed",
        StringComparison.Ordinal) ||
    string.Equals(
        command,
        "preview-legacy-invoice-test-seed",
        StringComparison.Ordinal) ||
    string.Equals(
        command,
        "inspect-legacy-invoice-test-seed-profile",
        StringComparison.Ordinal))
{
    if (canonicalizationCommitted)
        return 0;
    Console.Error.WriteLine(
        BuildSanitizedCanonicalizationError(ex));
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

static string BuildSanitizedCanonicalizationError(Exception exception)
{
    const string fallbackReasonCode = "unexpected_failure";
    var reasonCode = fallbackReasonCode;
    string? groupFingerprintSha256 = null;
    string? evidenceSha256 = null;

    if (exception is IsolatedLegacyInvoiceSeedCanonicalizationException
        canonicalizationException)
    {
        var candidateReasonCode = canonicalizationException.ReasonCode;
        if (
            !string.IsNullOrWhiteSpace(candidateReasonCode) &&
            candidateReasonCode.Length <= 128 &&
            candidateReasonCode.All(character =>
                character is >= 'a' and <= 'z' or
                    >= '0' and <= '9' or '_'))
        {
            reasonCode = candidateReasonCode;
        }

        var candidateFingerprint =
            canonicalizationException.GroupFingerprintSha256;
        if (
            candidateFingerprint is { Length: 64 } &&
            candidateFingerprint.All(character =>
                character is >= 'A' and <= 'F' or
                    >= '0' and <= '9'))
        {
            groupFingerprintSha256 = candidateFingerprint;
        }

        var candidateEvidenceSha256 =
            canonicalizationException.EvidenceSha256;
        if (
            candidateEvidenceSha256 is { Length: 64 } &&
            candidateEvidenceSha256.All(character =>
                character is >= 'A' and <= 'F' or
                    >= '0' and <= '9'))
        {
            evidenceSha256 = candidateEvidenceSha256;
        }
    }

    var sanitized =
        $"legacy_invoice_seed_canonicalization_failed reason_code={reasonCode}";
    if (groupFingerprintSha256 is not null)
    {
        sanitized +=
            $" group_fingerprint_sha256={groupFingerprintSha256}";
    }
    if (evidenceSha256 is not null)
        sanitized += $" evidence_sha256={evidenceSha256}";

    return sanitized;
}

static string BuildSanitizedSnapshotError(Exception exception)
{
    var reasonCode = exception switch
    {
        FileNotFoundException => "source_missing",
        DirectoryNotFoundException => "target_directory_missing",
        UnauthorizedAccessException => "filesystem_access_denied",
        IOException => "filesystem_race_or_lock",
        _ when exception.Message.Contains(
            "already exists",
            StringComparison.OrdinalIgnoreCase) => "target_exists",
        _ when exception.Message.Contains(
            "hard link",
            StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains(
                "NumberOfLinks",
                StringComparison.OrdinalIgnoreCase) => "hardlink_rejected",
        _ when exception.Message.Contains(
            "reparse",
            StringComparison.OrdinalIgnoreCase) => "reparse_rejected",
        _ when exception.Message.Contains(
            "sidecar",
            StringComparison.OrdinalIgnoreCase) => "sidecar_changed",
        _ when exception.Message.Contains(
            "identity",
            StringComparison.OrdinalIgnoreCase) => "identity_changed",
        _ => "snapshot_rejected"
    };
    return $"snapshot_sqlite_failed reason_code={reasonCode}";
}

static string BuildImmutableInspectionConnectionString(
    string databasePath)
{
    var normalizedDatabasePath = Path.GetFullPath(databasePath);
    if (!File.Exists(normalizedDatabasePath))
    {
        throw new FileNotFoundException(
            "The SQLite inspection source does not exist.",
            normalizedDatabasePath);
    }

    var sidecars = new[] { "-wal", "-shm", "-journal" }
        .Select(suffix => normalizedDatabasePath + suffix)
        .Where(File.Exists)
        .ToList();
    if (sidecars.Count > 0)
    {
        throw new InvalidOperationException(
            "SQLite inspection requires a finalized sidecar-free database. " +
            "Finalize the isolated app database before inspection. " +
            $"Found: {string.Join(", ", sidecars.Select(Path.GetFileName))}");
    }

    var immutableDatabaseUri =
        new Uri(normalizedDatabasePath).AbsoluteUri +
        "?immutable=1";
    return new SqliteConnectionStringBuilder
    {
        DataSource = immutableDatabaseUri,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString();
}

static async Task<int> PrintReadOnlyLegacyInvoiceSeedProfileAsync(
    string databasePath)
{
    try
    {
        using var inspectionGuard =
            ImmutableSqliteInspectionGuard.Acquire(databasePath);
        var connectionString = BuildImmutableInspectionConnectionString(
            inspectionGuard.DatabasePath);
        var inspectionOptions =
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .Options;
        await using var inspectionDb =
            new LocalDbContext(inspectionOptions);
        var preview = await IsolatedLegacyInvoiceSeedCanonicalizer
            .PreviewReadOnlyProfileAsync(
                inspectionDb,
                inspectionGuard.InitialSha256);

        inspectionGuard.AssertStableSidecarFree();
        Console.WriteLine(
            "legacy_invoice_seed_read_only_profile_succeeded=True");
        Console.WriteLine(
            $"legacy_invoice_seed_read_only_profile_sha256={preview.ComputeSha256()}");
        Console.WriteLine(
            $"legacy_invoice_seed_read_only_profile_json={preview.ToDeterministicJson()}");
        Console.WriteLine(
            $"legacy_invoice_seed_source_database_sha256={inspectionGuard.InitialSha256}");
        Console.WriteLine(
            $"legacy_invoice_seed_scope={IsolatedLegacyInvoiceSeedCanonicalizer.ActiveOperationalSeedScope}");
        return 0;
    }
    catch (Exception ex)
    {
        var reasonCode = ex switch
        {
            IsolatedLegacyInvoiceSeedCanonicalizationException canonicalizationError =>
                canonicalizationError.ReasonCode,
            FileNotFoundException => "source_missing",
            UnauthorizedAccessException => "source_access_denied",
            InvalidOperationException => "source_not_immutable",
            ArgumentException => "source_invalid",
            _ => "profile_rejected"
        };
        Console.Error.WriteLine(
            $"legacy_invoice_seed_read_only_profile_failed reason_code={reasonCode}");
        return 1;
    }
}

static async Task PrintDirtyInspectionAsync(LocalDbContext db)
{
    const int maxDetailRows = 25;
    var currentScopeDirty = await TryCountCurrentScopeDirtyAsync(db);
    if (currentScopeDirty.HasValue)
    {
        Console.WriteLine($"current_scope_dirty={currentScopeDirty.Value}");
    }

    Console.WriteLine("dirty_scope_note=current_scope_dirty is the authoritative value for the current login; all_scope_* values include cached tenants/offices outside the current login scope.");
    Console.WriteLine($"all_scope_customers_dirty={await CountDirtyAsync(db.Customers.IgnoreQueryFilters())}");
    Console.WriteLine($"all_scope_contracts_dirty={await CountDirtyAsync(db.CustomerContracts.IgnoreQueryFilters())}");
    var dirtyItemQuery = db.Items
        .IgnoreQueryFilters()
        .AsNoTracking()
        .Where(item => item.IsDirty);
    var dirtyItemCount = await dirtyItemQuery.CountAsync();
    var dirtyItems = await dirtyItemQuery
        .OrderBy(item => item.Id)
        .Select(item => new
        {
            item.Id,
            item.TenantCode,
            item.OfficeCode,
            item.Revision,
            item.UpdatedAtUtc
        })
        .Take(maxDetailRows)
        .ToListAsync();
    Console.WriteLine($"all_scope_items_dirty={dirtyItemCount}");
    Console.WriteLine($"dirty_item_detail_count={dirtyItems.Count}");
    Console.WriteLine(
        $"dirty_item_details_truncated={dirtyItemCount > dirtyItems.Count}");
    foreach (var item in dirtyItems)
    {
        Console.WriteLine(
            "dirty_item=" +
            $"{item.Id:D}|tenant={item.TenantCode}|office={item.OfficeCode}|" +
            $"revision={item.Revision}|updated_at_utc={item.UpdatedAtUtc:O}");
    }
    Console.WriteLine($"all_scope_invoices_dirty={await CountDirtyAsync(db.Invoices.IgnoreQueryFilters())}");
    Console.WriteLine($"all_scope_payments_dirty={await CountDirtyAsync(db.Payments.IgnoreQueryFilters())}");
    Console.WriteLine($"all_scope_transactions_dirty={await CountDirtyAsync(db.Transactions.IgnoreQueryFilters())}");
    var dirtyRentalManagementCompanyQuery =
        db.RentalManagementCompanies
        .IgnoreQueryFilters()
        .AsNoTracking()
        .Where(company => company.IsDirty);
    var dirtyRentalManagementCompanyCount =
        await dirtyRentalManagementCompanyQuery.CountAsync();
    var dirtyRentalManagementCompanies =
        await dirtyRentalManagementCompanyQuery
        .OrderBy(company => company.Id)
        .Select(company => new
        {
            company.Id,
            company.Code,
            company.IsSystemDefault,
            company.Revision,
            company.UpdatedAtUtc
        })
        .Take(maxDetailRows)
        .ToListAsync();
    Console.WriteLine(
        $"all_scope_rental_management_companies_dirty={dirtyRentalManagementCompanyCount}");
    Console.WriteLine(
        $"dirty_rental_management_company_detail_count={dirtyRentalManagementCompanies.Count}");
    Console.WriteLine(
        "dirty_rental_management_company_details_truncated=" +
        $"{dirtyRentalManagementCompanyCount > dirtyRentalManagementCompanies.Count}");
    foreach (var company in dirtyRentalManagementCompanies)
    {
        Console.WriteLine(
            "dirty_rental_management_company=" +
            $"{company.Id:D}|code={company.Code}|system_default={company.IsSystemDefault}|" +
            $"revision={company.Revision}|updated_at_utc={company.UpdatedAtUtc:O}");
    }
    Console.WriteLine($"all_scope_rental_profiles_dirty={await CountDirtyAsync(db.RentalBillingProfiles.IgnoreQueryFilters())}");
    var dirtyRentalAssetQuery = db.RentalAssets
        .IgnoreQueryFilters()
        .AsNoTracking()
        .Where(asset => asset.IsDirty);
    var dirtyRentalAssetCount =
        await dirtyRentalAssetQuery.CountAsync();
    var dirtyRentalAssets = await dirtyRentalAssetQuery
        .OrderBy(asset => asset.Id)
        .Select(asset => new
        {
            asset.Id,
            asset.TenantCode,
            asset.OfficeCode,
            asset.ResponsibleOfficeCode,
            asset.Revision,
            asset.UpdatedAtUtc
        })
        .Take(maxDetailRows)
        .ToListAsync();
    Console.WriteLine(
        $"all_scope_rental_assets_dirty={dirtyRentalAssetCount}");
    Console.WriteLine(
        $"dirty_rental_asset_detail_count={dirtyRentalAssets.Count}");
    Console.WriteLine(
        $"dirty_rental_asset_details_truncated={dirtyRentalAssetCount > dirtyRentalAssets.Count}");
    foreach (var asset in dirtyRentalAssets)
    {
        Console.WriteLine(
            "dirty_rental_asset=" +
            $"{asset.Id:D}|tenant={asset.TenantCode}|office={asset.OfficeCode}|" +
            $"responsible_office={asset.ResponsibleOfficeCode}|revision={asset.Revision}|" +
            $"updated_at_utc={asset.UpdatedAtUtc:O}");
    }
    var dirtyRentalAssetHistoryQuery =
        db.RentalAssetAssignmentHistories
        .IgnoreQueryFilters()
        .AsNoTracking()
        .Where(history => history.IsDirty);
    var dirtyRentalAssetHistoryCount =
        await dirtyRentalAssetHistoryQuery.CountAsync();
    var dirtyRentalAssetHistories =
        await dirtyRentalAssetHistoryQuery
        .OrderBy(history => history.Id)
        .Select(history => new
        {
            history.Id,
            history.TenantCode,
            history.ResponsibleOfficeCode,
            history.Revision,
            history.UpdatedAtUtc
        })
        .Take(maxDetailRows)
        .ToListAsync();
    Console.WriteLine(
        $"all_scope_rental_asset_histories_dirty={dirtyRentalAssetHistoryCount}");
    Console.WriteLine(
        $"dirty_rental_asset_history_detail_count={dirtyRentalAssetHistories.Count}");
    Console.WriteLine(
        "dirty_rental_asset_history_details_truncated=" +
        $"{dirtyRentalAssetHistoryCount > dirtyRentalAssetHistories.Count}");
    foreach (var history in dirtyRentalAssetHistories)
    {
        Console.WriteLine(
            "dirty_rental_asset_history=" +
            $"{history.Id:D}|tenant={history.TenantCode}|" +
            $"responsible_office={history.ResponsibleOfficeCode}|" +
            $"revision={history.Revision}|updated_at_utc={history.UpdatedAtUtc:O}");
    }
    Console.WriteLine($"all_scope_outbox_count={await db.SyncOutboxEntries.CountAsync()}");
}

static async Task<int?> TryCountCurrentScopeDirtyAsync(LocalDbContext db)
{
    var username = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_USERNAME");
    var password = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_PASSWORD");
    var baseUrl = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_BASEURL");

    if (string.IsNullOrWhiteSpace(username) ||
        string.IsNullOrEmpty(password) ||
        string.IsNullOrWhiteSpace(baseUrl))
    {
        return null;
    }

    var session = new SessionState();
    using var http = new HttpClient
    {
        BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(120)
    };

    var api = new ErpApiClient(http, session);
    var login = await api.LoginAsync(username, password);
    if (login is null || string.IsNullOrWhiteSpace(login.Token))
    {
        throw new InvalidOperationException("inspect_login_failed=True");
    }

    session.SetSession(login.Token, login.User);
    var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
    return await local.CountDirtyAsync(session);
}

static Task<int> CountDirtyAsync<TEntity>(IQueryable<TEntity> query)
    where TEntity : class, ILocalSyncEntity
    => query.CountAsync(entity => entity.IsDirty);

static async Task PrepareTestSeedAsync(LocalDbContext db)
{
    Directory.CreateDirectory(Path.GetDirectoryName(거래플랜.Desktop.App.Infrastructure.AppPaths.LocalDbFile)!);
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
    Console.WriteLine("prepare_ok=True");
}

static async Task PrintStoredCredentialEnvelopesAsync(string databasePath)
{
    var credentials =
        await IsolatedStoredCredentialReader.ReadAsync(databasePath);
    var payload = new
    {
        schemaVersion = 1,
        protection = "DPAPI-CurrentUser",
        credentials = credentials.Select(credential => new
        {
            credential.OfficeCode,
            credential.TenantCode,
            credential.Username,
            credential.PasswordProtected,
            SavedAtUtc = credential.SavedAtUtc.ToString("O")
        })
    };

    Console.WriteLine(JsonSerializer.Serialize(payload));
}

static async Task PrintSourceCredentialEnvelopesAsync(string databasePath)
{
    var candidates = new List<IsolatedStoredCredential>();
    var savedLogin =
        await IsolatedStoredCredentialReader.ReadSavedLoginAsync(databasePath);
    if (savedLogin is not null)
        candidates.Add(savedLogin);

    candidates.AddRange(
        await IsolatedStoredCredentialReader.ReadAsync(databasePath));
    var uniqueCandidates = candidates
        .GroupBy(
            credential => credential.Username,
            StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .Take(IsolatedStoredCredentialReader.MaximumCredentialCount)
        .ToList();
    var payload = new
    {
        schemaVersion = 1,
        protection = "DPAPI-CurrentUser",
        credentials = uniqueCandidates.Select(credential => new
        {
            credential.OfficeCode,
            credential.TenantCode,
            credential.Username,
            credential.PasswordProtected,
            SavedAtUtc = credential.SavedAtUtc.ToString("O")
        })
    };

    Console.WriteLine(JsonSerializer.Serialize(payload));
}

static async Task<int> RunSyncAsync(LocalDbContext db)
{
    var username = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_USERNAME");
    var password = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_PASSWORD");
    var baseUrl = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_BASEURL");

    if (string.IsNullOrWhiteSpace(username) ||
        string.IsNullOrEmpty(password) ||
        string.IsNullOrWhiteSpace(baseUrl))
    {
        Console.Error.WriteLine("GEORAEPLAN_SYNC_USERNAME/PASSWORD/BASEURL 환경변수가 필요합니다.");
        return 1;
    }

    var session = new SessionState();
    using var http = new HttpClient
    {
        BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(120)
    };

    var api = new ErpApiClient(http, session);
    var login = await api.LoginAsync(username, password);
    if (login is null || string.IsNullOrWhiteSpace(login.Token))
    {
        Console.Error.WriteLine("login_failed=True");
        return 1;
    }

    session.SetSession(login.Token, login.User);
    var dispatcher = new SyncRequestDispatcher();
    var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
    var rental = new RentalStateService(db, local);
    var diagnostics = new SyncDiagnosticsService(session);
    using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);

    var ok = await sync.TrySyncAsync();
    Console.WriteLine($"sync_ok={ok}");
    Console.WriteLine($"dirty_count={await local.CountDirtyAsync(session)}");
    Console.WriteLine(
        $"non_acknowledged_outbox_count={await db.SyncOutboxEntries.CountAsync(entry => entry.Status != "Acknowledged")}");
    if (!ok &&
        IsTruthy(
            Environment.GetEnvironmentVariable(
                "GEORAEPLAN_TEST_SEED_MODE")))
    {
        foreach (var line in
                 await IsolatedSeedSyncFailureDiagnostics.BuildLinesAsync(db))
        {
            Console.WriteLine(line);
        }
    }
    return ok ? 0 : 1;
}

static async Task<int> MarkAllDirtyAsync(LocalDbContext db)
{
    await using var transaction = await db.Database.BeginTransactionAsync();
    var normalizedUnknownBillingStatuses =
        await IsolatedSeedRentalAssetBillingStatusNormalizer.NormalizeAsync(db);
    Console.WriteLine(
        $"normalized_unknown_rental_billing_statuses={normalizedUnknownBillingStatuses}");
    await db.SyncOutboxEntries.ExecuteDeleteAsync();
    var count = 0;
    count += await MarkDirtyAsync(db.CustomerMasters.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.Customers.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.CustomerContracts.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.Items.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.Invoices.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.Payments.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.Transactions.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.TransactionAttachments.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.InventoryTransfers.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.RentalManagementCompanies.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.RentalBillingProfiles.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.RentalAssets.IgnoreQueryFilters());
    count += await MarkValidRentalAssetAssignmentHistoriesDirtyAsync(db);
    count += await MarkDirtyAsync(db.RentalBillingLogs.IgnoreQueryFilters());
    await transaction.CommitAsync();
    return count;
}

static async Task<int> MarkDirtyAsync<TEntity>(IQueryable<TEntity> query)
    where TEntity : class, ILocalSyncEntity
{
    await query
        .Where(entity => entity.IsDeleted && entity.IsDirty)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(entity => entity.IsDirty, false));

    var now = DateTime.UtcNow;
    return await query
        .Where(entity => !entity.IsDeleted)
        .ExecuteUpdateAsync(setters => setters
        .SetProperty(entity => entity.IsDirty, true)
        .SetProperty(entity => entity.UpdatedAtUtc, now));
}

static async Task<int> MarkValidRentalAssetAssignmentHistoriesDirtyAsync(LocalDbContext db)
{
    var activeAssetIds = await db.RentalAssets
        .IgnoreQueryFilters()
        .Where(asset => !asset.IsDeleted)
        .Select(asset => asset.Id)
        .ToListAsync();
    var histories = db.RentalAssetAssignmentHistories.IgnoreQueryFilters();
    await histories
        .Where(history =>
            history.IsDirty &&
            (history.IsDeleted || !activeAssetIds.Contains(history.AssetId)))
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(history => history.IsDirty, false));

    return await MarkDirtyAsync(
        histories.Where(history => activeAssetIds.Contains(history.AssetId)));
}

static async Task<(
    int RebasedInvoices,
    int RebasedPayments,
    int RebasedTransactions,
    int UnlinkedExcludedRentalAssets,
    int ClosedRentalAssignmentHistories,
    int RemovedCollateralFailedAssignmentOutbox,
    int RemovedSupersededSentAssignmentOutbox,
    int RemovedStaleOutbox,
    int RemovedCleanOutbox)>
    PrepareTestSeedRetryAsync(LocalDbContext db)
{
    await using var dbTransaction = await db.Database.BeginTransactionAsync();
    var retryNowUtc = DateTime.UtcNow;
    var serverDatabasePath = Path.Combine(
        Environment.GetEnvironmentVariable(
            "GEORAEPLAN_TEST_SERVER_ROOT") ?? string.Empty,
        "거래플랜-local.db");
    var rentalAssetReconciliation =
        await IsolatedSeedRetryRentalAssetReconciler.ReconcileAsync(
            db,
            retryNowUtc);
    var invoiceVersionMetadataReconciliation =
        await IsolatedSeedRetryInvoiceVersionMetadataReconciler
            .ReconcileAsync(
                db,
                serverDatabasePath);
    var invoiceRevisions = await db.Invoices
        .IgnoreQueryFilters()
        .Where(invoice => !invoice.IsDeleted)
        .Select(invoice => new { invoice.Id, invoice.Revision })
        .ToDictionaryAsync(invoice => invoice.Id, invoice => invoice.Revision);

    var payments = await db.Payments
        .IgnoreQueryFilters()
        .Where(payment => payment.IsDirty && !payment.IsDeleted)
        .ToListAsync();
    var rebasedPaymentIds = new List<Guid>();
    foreach (var payment in payments)
    {
        if (!invoiceRevisions.TryGetValue(payment.InvoiceId, out var invoiceRevision) ||
            invoiceRevision <= 0 ||
            payment.Revision == invoiceRevision)
        {
            continue;
        }

        payment.Revision = invoiceRevision;
        rebasedPaymentIds.Add(payment.Id);
    }

    var transactions = await db.Transactions
        .IgnoreQueryFilters()
        .Where(transaction =>
            transaction.IsDirty &&
            !transaction.IsDeleted &&
            transaction.LinkedInvoiceId.HasValue &&
            transaction.LinkedInvoiceId.Value != Guid.Empty)
        .ToListAsync();
    var rebasedTransactionIds = new List<Guid>();
    foreach (var transaction in transactions)
    {
        if (!transaction.LinkedInvoiceId.HasValue ||
            !invoiceRevisions.TryGetValue(transaction.LinkedInvoiceId.Value, out var invoiceRevision) ||
            invoiceRevision <= 0 ||
            transaction.Revision == invoiceRevision)
        {
            continue;
        }

        transaction.Revision = invoiceRevision;
        rebasedTransactionIds.Add(transaction.Id);
    }

    var removedStaleOutbox =
        rentalAssetReconciliation.RemovedStaleOutbox +
        invoiceVersionMetadataReconciliation.RemovedStaleOutbox;
    if (rebasedPaymentIds.Count > 0)
    {
        removedStaleOutbox += await db.SyncOutboxEntries
            .Where(entry =>
                entry.Status != "Acknowledged" &&
                entry.EntityName == nameof(LocalPayment) &&
                rebasedPaymentIds.Contains(entry.EntityId))
            .ExecuteDeleteAsync();
    }

    if (rebasedTransactionIds.Count > 0)
    {
        removedStaleOutbox += await db.SyncOutboxEntries
            .Where(entry =>
                entry.Status != "Acknowledged" &&
                entry.EntityName == nameof(LocalTransaction) &&
                rebasedTransactionIds.Contains(entry.EntityId))
            .ExecuteDeleteAsync();
    }

    var removedCollateralFailedAssignmentOutbox =
        await IsolatedSeedRetryOutboxReconciler
            .RemoveExactFailedOutboxForDirtyEntitiesAsync<LocalRentalAssetAssignmentHistory>(db);
    var removedSupersededSentAssignmentOutbox =
        await IsolatedSeedRetryOutboxReconciler
            .SupersedeUniqueSentOutboxForDirtyEntitiesAsync<LocalRentalAssetAssignmentHistory>(
                db,
                retryNowUtc);

    var removedCleanOutbox = 0;
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalCompanyProfile>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalUnit>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalCustomerCategory>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalPriceGradeOption>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalTradeTypeOption>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalItemCategoryOption>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalCustomerMaster>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalCustomer>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalCustomerContract>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalItem>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalItemPriceGrade>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalTransaction>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalTransactionAttachment>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalInventoryTransfer>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalRentalManagementCompany>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalRentalBillingProfile>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalRentalAsset>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalRentalAssetAssignmentHistory>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalRentalBillingLog>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalInvoice>(db);
    removedCleanOutbox +=
        await RemoveCleanOutboxAsync<LocalPayment>(db);

    await db.SaveChangesAsync();
    await dbTransaction.CommitAsync();
    return (
        invoiceVersionMetadataReconciliation.RebasedInvoices,
        rebasedPaymentIds.Count,
        rebasedTransactionIds.Count,
        rentalAssetReconciliation.UnlinkedAssets,
        rentalAssetReconciliation.ClosedAssignmentHistories,
        removedCollateralFailedAssignmentOutbox,
        removedSupersededSentAssignmentOutbox,
        removedStaleOutbox,
        removedCleanOutbox);
}

static async Task<int> RemoveCleanOutboxAsync<TEntity>(
    LocalDbContext db)
    where TEntity : class, ILocalSyncEntity
    => await IsolatedSeedRetryOutboxReconciler
        .RemoveCleanOutboxAsync<TEntity>(db);

static bool RequiresIsolatedTestDatabaseLease(string? value)
{
    if (IsAlwaysIsolatedTestSeedCommand(value))
        return true;

    if (!IsTruthy(
            Environment.GetEnvironmentVariable(
                "GEORAEPLAN_TEST_SEED_MODE")))
    {
        return false;
    }

    return string.Equals(value, "preseed-sync", StringComparison.Ordinal) ||
           string.Equals(value, "sync", StringComparison.Ordinal) ||
           string.Equals(value, "maintenance-sync", StringComparison.Ordinal) ||
           string.Equals(value, "inspect", StringComparison.Ordinal);
}

static bool IsAlwaysIsolatedTestSeedCommand(string? value)
    => string.Equals(value, "prepare-test-seed", StringComparison.Ordinal) ||
       string.Equals(
           value,
           "inspect-legacy-invoice-test-seed-profile",
           StringComparison.Ordinal) ||
       string.Equals(
           value,
           "preview-legacy-invoice-test-seed",
           StringComparison.Ordinal) ||
       string.Equals(
           value,
           "canonicalize-legacy-invoice-test-seed",
           StringComparison.Ordinal) ||
       string.Equals(value, "prepare-test-seed-retry", StringComparison.Ordinal) ||
       string.Equals(value, "mark-all-dirty", StringComparison.Ordinal) ||
       string.Equals(
           value,
           "stored-credential-envelopes",
           StringComparison.Ordinal) ||
       string.Equals(
           value,
           "source-credential-envelopes",
           StringComparison.Ordinal);

static bool RequiresIsolatedTestServerTargetGuard(string? value)
    => IsTruthy(
           Environment.GetEnvironmentVariable(
               "GEORAEPLAN_TEST_SEED_MODE")) &&
       (string.Equals(value, "prepare-test-seed-retry", StringComparison.Ordinal) ||
        string.Equals(value, "preseed-sync", StringComparison.Ordinal) ||
        string.Equals(value, "sync", StringComparison.Ordinal) ||
        string.Equals(value, "maintenance-sync", StringComparison.Ordinal));

static IsolatedTestServerTargetGuard
    AcquireIsolatedTestServerTargetGuard()
{
    var rawBaseUrl =
        Environment.GetEnvironmentVariable(
            "GEORAEPLAN_SYNC_BASEURL");
    var attestedBaseUrl =
        Environment.GetEnvironmentVariable(
            "GEORAEPLAN_TEST_SERVER_BASEURL");
    if (!Uri.TryCreate(
            rawBaseUrl,
            UriKind.Absolute,
            out var baseUri) ||
        !baseUri.IsLoopback ||
        !string.Equals(
            baseUri.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            baseUri.AbsolutePath,
            "/",
            StringComparison.Ordinal) ||
        !string.IsNullOrEmpty(baseUri.Query) ||
        !string.IsNullOrEmpty(baseUri.Fragment))
    {
        throw new InvalidOperationException(
            "Isolated test-seed sync requires an explicit HTTP loopback target.");
    }

    var normalizedBaseUrl =
        baseUri.GetLeftPart(UriPartial.Authority)
            .TrimEnd('/');
    if (!Uri.TryCreate(
            attestedBaseUrl,
            UriKind.Absolute,
            out var attestedUri) ||
        !attestedUri.IsLoopback ||
        !string.Equals(
            attestedUri.AbsolutePath,
            "/",
            StringComparison.Ordinal) ||
        !string.IsNullOrEmpty(attestedUri.Query) ||
        !string.IsNullOrEmpty(attestedUri.Fragment) ||
        !string.Equals(
            normalizedBaseUrl,
            attestedUri.GetLeftPart(UriPartial.Authority)
                .TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "The isolated test server base URL attestation does not match the sync target.");
    }

    var serverRoot =
        Environment.GetEnvironmentVariable(
            "GEORAEPLAN_TEST_SERVER_ROOT");
    var appRoot =
        Environment.GetEnvironmentVariable(
            "GEORAEPLAN_TEST_SEED_ROOT");
    if (string.IsNullOrWhiteSpace(serverRoot) ||
        string.IsNullOrWhiteSpace(appRoot))
    {
        throw new InvalidOperationException(
            "Isolated test-seed sync requires explicit app and server roots.");
    }

    var normalizedServerRoot =
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(serverRoot));
    var normalizedAppRoot =
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(appRoot));
    var serverParent =
        Directory.GetParent(normalizedServerRoot)?.FullName;
    var appParent =
        Directory.GetParent(normalizedAppRoot)?.FullName;
    if (string.IsNullOrWhiteSpace(serverParent) ||
        string.IsNullOrWhiteSpace(appParent) ||
        !string.Equals(
            Path.TrimEndingDirectorySeparator(serverParent),
            Path.TrimEndingDirectorySeparator(appParent),
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "The isolated app and server roots do not share the same preparation root.");
    }

    var markerPath = Path.Combine(
        normalizedServerRoot,
        ".georaeplan-isolated-server-root");
    if (!File.Exists(markerPath) ||
        (File.GetAttributes(markerPath) &
         FileAttributes.ReparsePoint) != 0)
    {
        throw new InvalidOperationException(
            "The isolated server root attestation marker is missing or mismatched.");
    }

    return IsolatedTestServerTargetGuard.Acquire(
        normalizedServerRoot,
        markerPath);
}

static void AssertIsolatedTestSeedCommandEnvironment()
{
    if (!IsTruthy(Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_MODE")) ||
        !IsTruthy(Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_SEED_MODE")))
    {
        throw new InvalidOperationException(
            "Test seed commands require GEORAEPLAN_TEST_MODE=1 and GEORAEPLAN_TEST_SEED_MODE=1.");
    }

    var appRootValue = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
    var expectedRootValue = Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_SEED_ROOT");
    if (string.IsNullOrWhiteSpace(appRootValue) ||
        string.IsNullOrWhiteSpace(expectedRootValue))
    {
        throw new InvalidOperationException(
            "Test seed commands require explicit GEORAEPLAN_APP_ROOT and GEORAEPLAN_TEST_SEED_ROOT values.");
    }

    var appRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appRootValue));
    var expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedRootValue));
    var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(appRoot) ?? string.Empty);
    if (string.IsNullOrWhiteSpace(volumeRoot) ||
        string.Equals(appRoot, volumeRoot, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(appRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "Test seed commands require the exact isolated AppData root below a volume root.");
    }

    AssertNoReparsePointAncestors(appRoot);

    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrWhiteSpace(localAppData))
    {
        var productionRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(localAppData, "거래플랜")));
        if (PathsOverlap(appRoot, productionRoot))
        {
            throw new InvalidOperationException(
                "Test seed commands cannot use or contain the normal V1 application data root.");
        }
    }

    var markerPath = Path.Combine(appRoot, ".georaeplan-isolated-seed-root");
    if (!File.Exists(markerPath))
    {
        throw new InvalidOperationException(
            $"The isolated test seed marker is missing: {markerPath}");
    }
    if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
    {
        throw new InvalidOperationException(
            "The isolated test seed marker cannot be a reparse point.");
    }

    var markerRoot = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(File.ReadAllText(markerPath).Trim()));
    if (!string.Equals(markerRoot, appRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "The isolated test seed marker does not match GEORAEPLAN_APP_ROOT.");
    }
}

static void AssertNoReparsePointAncestors(string appRoot)
{
    var current = new DirectoryInfo(appRoot);
    while (current is not null)
    {
        if (!current.Exists)
        {
            throw new InvalidOperationException(
                $"The isolated test seed path does not exist: {current.FullName}");
        }
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Test seed commands reject reparse-point paths: {current.FullName}");
        }

        current = current.Parent;
    }
}

static bool IsTruthy(string? value)
    => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
       string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
       string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
       string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

static bool PathsOverlap(string left, string right)
{
    if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        return true;

    var leftPrefix = left + Path.DirectorySeparatorChar;
    var rightPrefix = right + Path.DirectorySeparatorChar;
    return left.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase) ||
           right.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase);
}

static SqliteSnapshotResult CreateStandaloneSqliteSnapshot(
    string sourceDatabasePath,
    string targetDatabasePath)
{
    var paths = ValidateSqliteSnapshotPaths(
        sourceDatabasePath,
        targetDatabasePath);
    using var snapshotTestPhases =
        SnapshotTestPhaseLease.Acquire(paths.TargetDatabasePath);
    AssertSnapshotTargetSidecarsAbsent(paths.TargetDatabasePath);
    var sourceHadSidecarsBeforeLease =
        SnapshotSidecarLeaseSet.HasAnyEntry(paths.SourceDatabasePath);
    using var sourceLease = SnapshotSourceFileLease.Acquire(
        paths.SourceDatabasePath,
        allowWriteSharing: sourceHadSidecarsBeforeLease);
    using var sourceSidecarLeases =
        SnapshotSidecarLeaseSet.Acquire(paths.SourceDatabasePath);
    var sourceFingerprint =
        CaptureSnapshotSourceFingerprint(
            paths.SourceDatabasePath,
            sourceLease,
            sourceSidecarLeases);
    var sourceWasSidecarFree =
        sourceFingerprint.Sidecars.Count == 0;
    if (sourceWasSidecarFree == !sourceHadSidecarsBeforeLease)
    {
        // The sidecar mode observed before and after acquiring the no-delete
        // source lease agrees.
    }
    else
    {
        throw new InvalidOperationException(
            "The source SQLite sidecar mode changed while acquiring its lease.");
    }

    var temporaryDatabasePath = paths.TargetDatabasePath +
                                $".snapshot-{Guid.NewGuid():N}.tmp";
    var ownsTemporaryDatabase = false;
    SnapshotSourceFileLease? temporaryLease = null;
    byte[] serializedDatabase = [];
    try
    {
        temporaryLease =
            SnapshotSourceFileLease.CreateOwned(
                temporaryDatabasePath);
        ownsTemporaryDatabase = true;
        temporaryLease.AssertStable();

        var sourceDataSource = sourceWasSidecarFree
            ? new Uri(paths.SourceDatabasePath).AbsoluteUri + "?immutable=1"
            : paths.SourceDatabasePath;
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();

        string quickCheck;
        using (var sourceConnection =
               new SqliteConnection(sourceConnectionString))
        using (var destinationConnection =
               new SqliteConnection(destinationConnectionString))
        {
            sourceConnection.Open();
            AssertSnapshotSourceStable(
                paths.SourceDatabasePath,
                sourceLease,
                sourceSidecarLeases,
                sourceFingerprint,
                sourceWasSidecarFree,
                "after source open");
            using (var queryOnly = sourceConnection.CreateCommand())
            {
                queryOnly.CommandText = "PRAGMA query_only=ON;";
                queryOnly.ExecuteNonQuery();
            }

            destinationConnection.Open();
            temporaryLease.AssertStable();
            sourceConnection.BackupDatabase(destinationConnection);
            snapshotTestPhases?.Wait("POST_SOURCE_BACKUP");
            temporaryLease.AssertStable();
            if (CountSqliteSidecars(temporaryDatabasePath) != 0)
            {
                throw new InvalidOperationException(
                    "An unexpected temporary SQLite sidecar appeared.");
            }
            AssertSnapshotSourceStable(
                paths.SourceDatabasePath,
                sourceLease,
                sourceSidecarLeases,
                sourceFingerprint,
                sourceWasSidecarFree,
                "after source backup");

            quickCheck = ExecuteScalarText(
                destinationConnection,
                "PRAGMA quick_check;");
            if (!string.Equals(
                    quickCheck,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The temporary snapshot failed SQLite quick_check.");
            }
            serializedDatabase =
                SerializeAttestedSqliteSnapshot(
                    destinationConnection,
                    sourceFingerprint);
            temporaryLease.ReplaceContent(serializedDatabase);
            temporaryLease.AssertStable();
        }
        temporaryLease.AssertStable();
        AssertSnapshotSourceStable(
            paths.SourceDatabasePath,
            sourceLease,
            sourceSidecarLeases,
            sourceFingerprint,
            sourceWasSidecarFree,
            "after source close");

        var sidecarCount = CountSqliteSidecars(temporaryDatabasePath);
        if (sidecarCount != 0)
        {
            throw new InvalidOperationException(
                "The temporary snapshot retained a SQLite sidecar.");
        }

        quickCheck =
            VerifySerializedSqliteSnapshot(serializedDatabase);
        var approvedTemporaryFingerprint =
            temporaryLease.CaptureFingerprint();
        AssertSerializedSnapshotFingerprint(
            serializedDatabase,
            approvedTemporaryFingerprint);
        temporaryLease.AssertStable();
        snapshotTestPhases?.Wait("POST_QUICK_CHECK");
        temporaryLease.AssertStable();
        sidecarCount = CountSqliteSidecars(temporaryDatabasePath);
        if (sidecarCount != 0)
        {
            throw new InvalidOperationException(
                "Standalone snapshot verification created a SQLite sidecar.");
        }

        var temporaryFingerprint = temporaryLease.CaptureFingerprint();
        if (temporaryFingerprint != approvedTemporaryFingerprint)
        {
            throw new InvalidOperationException(
                "The approved temporary SQLite snapshot fingerprint changed.");
        }
        var targetLength = temporaryFingerprint.Length;
        if (targetLength <= 0)
        {
            throw new InvalidOperationException(
                "The temporary SQLite snapshot is empty.");
        }
        var targetSha256 = temporaryFingerprint.Sha256;

        AssertSnapshotSourceStable(
            paths.SourceDatabasePath,
            sourceLease,
            sourceSidecarLeases,
            sourceFingerprint,
            sourceWasSidecarFree,
            "before target commit");
        AssertSnapshotTargetAbsent(paths.TargetDatabasePath);
        temporaryLease.AssertStable();
        if (!string.Equals(
                Path.GetPathRoot(temporaryDatabasePath),
                Path.GetPathRoot(paths.TargetDatabasePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The temporary and target snapshot databases must share a volume.");
        }

        var preRenameFingerprint =
            temporaryLease.CaptureFingerprint();
        if (preRenameFingerprint != approvedTemporaryFingerprint)
        {
            throw new InvalidOperationException(
                "The approved temporary SQLite snapshot changed before protected rename.");
        }
        temporaryLease.MoveTo(
            paths.TargetDatabasePath,
            () => snapshotTestPhases?.InjectFailure(
                "POST_RENAME_STATE_TRANSITION"),
            () => snapshotTestPhases
                      ?.InjectPersistentValidationFailure(
                          "POST_RENAME_PERSISTENT_VALIDATION_FAILURE")
                  ?? false);
        var committedFingerprint =
            temporaryLease.CaptureFingerprint();
        if (committedFingerprint.Length != targetLength ||
            !string.Equals(
                committedFingerprint.Sha256,
                targetSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The committed SQLite snapshot identity or hash changed.");
        }
        snapshotTestPhases?.Wait("POST_COMMITTED_FINGERPRINT");
        temporaryLease.AssertStable();
        var finalCommittedFingerprint =
            temporaryLease.CaptureFingerprint();
        if (CountSqliteSidecars(paths.TargetDatabasePath) != 0 ||
            finalCommittedFingerprint.Length != targetLength ||
            !string.Equals(
                finalCommittedFingerprint.Sha256,
                targetSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The committed SQLite snapshot verification changed.");
        }

        var result = new SqliteSnapshotResult(
            targetLength,
            targetSha256,
            quickCheck,
            sidecarCount);
        ownsTemporaryDatabase = false;
        temporaryLease.Dispose();
        temporaryLease = null;
        return result;
    }
    catch
    {
        if (temporaryLease is not null)
        {
            if (ownsTemporaryDatabase)
            {
                try
                {
                    temporaryLease.DeleteOwnedFile();
                }
                catch
                {
                    temporaryLease.Dispose();
                }
            }
            else
            {
                temporaryLease.Dispose();
            }
        }
        throw;
    }
    finally
    {
        if (serializedDatabase.Length != 0)
            CryptographicOperations.ZeroMemory(serializedDatabase);
    }
}

static SqliteSnapshotPaths ValidateSqliteSnapshotPaths(
    string sourceDatabasePath,
    string targetDatabasePath)
{
    if (!IsTruthy(
            Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_MODE")))
    {
        throw new InvalidOperationException(
            "SQLite snapshot creation requires GEORAEPLAN_TEST_MODE=1.");
    }

    var sourceRoot = NormalizeRequiredSnapshotRoot(
        Environment.GetEnvironmentVariable(
            "GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"),
        "GEORAEPLAN_SOURCE_SNAPSHOT_ROOT");
    var targetRoot = NormalizeRequiredSnapshotRoot(
        Environment.GetEnvironmentVariable(
            "GEORAEPLAN_TARGET_SNAPSHOT_ROOT"),
        "GEORAEPLAN_TARGET_SNAPSHOT_ROOT");
    if (PathsOverlap(sourceRoot, targetRoot))
    {
        throw new InvalidOperationException(
            "The source and target snapshot roots must be separate.");
    }

    if (!string.Equals(
            Path.GetPathRoot(targetRoot),
            @"D:\",
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "The target snapshot root must be on D:.");
    }

    if (!Path.IsPathFullyQualified(sourceDatabasePath) ||
        !Path.IsPathFullyQualified(targetDatabasePath))
    {
        throw new InvalidOperationException(
            "SQLite snapshot database paths must be fully qualified.");
    }

    var normalizedSourceDatabasePath =
        Path.GetFullPath(sourceDatabasePath);
    var normalizedTargetDatabasePath =
        Path.GetFullPath(targetDatabasePath);
    var expectedSourceDatabasePath = Path.Combine(
        sourceRoot,
        "data",
        IsolatedPreparationDatabaseLease.LocalDatabaseFileName);
    var expectedTargetDatabasePath = Path.Combine(
        targetRoot,
        "data",
        IsolatedPreparationDatabaseLease.LocalDatabaseFileName);
    if (!string.Equals(
            normalizedSourceDatabasePath,
            expectedSourceDatabasePath,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "The source database must exactly match the configured source snapshot root.");
    }
    if (!string.Equals(
            normalizedTargetDatabasePath,
            expectedTargetDatabasePath,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "The target database must exactly match the configured target snapshot root.");
    }
    if (string.Equals(
            normalizedSourceDatabasePath,
            normalizedTargetDatabasePath,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "The source and target snapshot databases must be different.");
    }

    var sourceDataDirectory =
        Path.GetDirectoryName(normalizedSourceDatabasePath)
        ?? throw new InvalidOperationException(
            "The source snapshot database must have a parent directory.");
    var targetDataDirectory =
        Path.GetDirectoryName(normalizedTargetDatabasePath)
        ?? throw new InvalidOperationException(
            "The target snapshot database must have a parent directory.");
    if (!File.Exists(normalizedSourceDatabasePath))
    {
        throw new FileNotFoundException(
            "The source SQLite database does not exist.");
    }
    if (!Directory.Exists(targetDataDirectory))
    {
        throw new DirectoryNotFoundException(
            "The target snapshot data directory does not exist.");
    }
    if (File.Exists(normalizedTargetDatabasePath) ||
        Directory.Exists(normalizedTargetDatabasePath))
    {
        throw new InvalidOperationException(
            "The target snapshot database already exists.");
    }

    AssertNoReparsePointAncestors(sourceDataDirectory);
    AssertNoReparsePointAncestors(targetDataDirectory);
    AssertRegularSnapshotFile(
        normalizedSourceDatabasePath,
        "The source SQLite database");

    return new SqliteSnapshotPaths(
        normalizedSourceDatabasePath,
        normalizedTargetDatabasePath);
}

static string NormalizeRequiredSnapshotRoot(
    string? value,
    string environmentVariableName)
{
    if (string.IsNullOrWhiteSpace(value) ||
        !Path.IsPathFullyQualified(value))
    {
        throw new InvalidOperationException(
            $"{environmentVariableName} must be an explicit absolute directory.");
    }

    var normalized = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(value));
    var volumeRoot = Path.TrimEndingDirectorySeparator(
        Path.GetPathRoot(normalized) ?? string.Empty);
    if (string.IsNullOrWhiteSpace(volumeRoot) ||
        string.Equals(
            normalized,
            volumeRoot,
            StringComparison.OrdinalIgnoreCase) ||
        !Directory.Exists(normalized))
    {
        throw new InvalidOperationException(
            $"{environmentVariableName} must identify an existing directory below a volume root.");
    }

    AssertNoReparsePointAncestors(normalized);
    return normalized;
}

static void AssertRegularSnapshotFile(
    string path,
    string description)
{
    var attributes = File.GetAttributes(path);
    if ((attributes & FileAttributes.ReparsePoint) != 0 ||
        (attributes & FileAttributes.Directory) != 0)
    {
        throw new InvalidOperationException(
            $"{description} must be a regular file.");
    }
}

static void AssertSnapshotTargetAbsent(
    string targetDatabasePath)
{
    AssertSnapshotTargetSidecarsAbsent(targetDatabasePath);
    if (File.Exists(targetDatabasePath) ||
        Directory.Exists(targetDatabasePath))
    {
        throw new InvalidOperationException(
            "The target snapshot database appeared during preparation.");
    }
}

static void AssertSnapshotTargetSidecarsAbsent(
    string targetDatabasePath)
{
    if (CountSqliteSidecars(targetDatabasePath) != 0)
    {
        throw new InvalidOperationException(
            "The target snapshot database must not have WAL, SHM, or journal sidecars.");
    }
}

static SnapshotSourceFingerprint CaptureSnapshotSourceFingerprint(
    string databasePath,
    SnapshotSourceFileLease sourceLease,
    SnapshotSidecarLeaseSet sidecarLeases)
{
    var database = sourceLease.CaptureFingerprint();
    sidecarLeases.AssertStable();
    var sidecars = sidecarLeases.CaptureFingerprints();
    return new SnapshotSourceFingerprint(database, sidecars);
}

static void AssertSnapshotSourceStable(
    string databasePath,
    SnapshotSourceFileLease sourceLease,
    SnapshotSidecarLeaseSet sidecarLeases,
    SnapshotSourceFingerprint expected,
    bool requireSidecarAbsence,
    string phase)
{
    sourceLease.AssertStable();
    var actual = CaptureSnapshotSourceFingerprint(
        databasePath,
        sourceLease,
        sidecarLeases);
    if (actual.Database != expected.Database)
    {
        throw new InvalidOperationException(
            $"The source SQLite database changed {phase}.");
    }

    if (requireSidecarAbsence && actual.Sidecars.Count != 0)
    {
        throw new InvalidOperationException(
            $"The immutable source SQLite database gained a sidecar {phase}.");
    }
    if (actual.Sidecars.Count != expected.Sidecars.Count)
    {
        throw new InvalidOperationException(
            $"The source SQLite sidecar set changed {phase}.");
    }

    for (var index = 0; index < expected.Sidecars.Count; index++)
    {
        var expectedSidecar = expected.Sidecars[index];
        var actualSidecar = actual.Sidecars[index];
        if (!string.Equals(
                actualSidecar.Suffix,
                expectedSidecar.Suffix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The source SQLite sidecar set changed {phase}.");
        }

        // SQLite readers can update transient read marks in SHM. WAL and
        // rollback-journal bytes remain authoritative for stability.
        if (!string.Equals(
                expectedSidecar.Suffix,
                ".db-shm",
                StringComparison.OrdinalIgnoreCase) &&
            actualSidecar != expectedSidecar)
        {
            throw new InvalidOperationException(
                $"An authoritative source SQLite sidecar changed {phase}.");
        }
    }
}

static int CountSqliteSidecars(string databasePath)
    => EnumerateSqliteSidecars(databasePath)
        .Count(SnapshotSourceFileLease.PathEntryExists);

static IEnumerable<string> EnumerateSqliteSidecars(string databasePath)
{
    yield return databasePath + "-wal";
    yield return databasePath + "-shm";
    yield return databasePath + "-journal";
}

static byte[] SerializeAttestedSqliteSnapshot(
    SqliteConnection connection,
    SnapshotSourceFingerprint sourceFingerprint)
{
    const long maximumSerializedSnapshotBytes =
        1024L * 1024L * 1024L;
    var pageCount =
        ExecuteScalarInt64(connection, "PRAGMA page_count;");
    var pageSize =
        ExecuteScalarInt64(connection, "PRAGMA page_size;");
    var expectedSerializedBytes =
        checked(pageCount * pageSize);
    var attestedSourceBytes =
        sourceFingerprint.Database.Length;
    foreach (var sidecar in sourceFingerprint.Sidecars)
    {
        attestedSourceBytes =
            checked(attestedSourceBytes + sidecar.Length);
    }

    if (pageCount <= 0 ||
        pageSize <= 0 ||
        expectedSerializedBytes <= 0 ||
        expectedSerializedBytes > attestedSourceBytes ||
        expectedSerializedBytes > maximumSerializedSnapshotBytes ||
        expectedSerializedBytes > int.MaxValue)
    {
        throw new InvalidOperationException(
            "The SQLite snapshot serialization size is not safely attested.");
    }

    var serializedPointer =
        SQLitePCL.raw.sqlite3_serialize(
            connection.Handle!,
            "main",
            out var serializedSize,
            flags: 0);
    var validatedSerializedSize = 0;
    try
    {
        if (serializedPointer != IntPtr.Zero &&
            serializedSize > 0 &&
            serializedSize <= maximumSerializedSnapshotBytes &&
            serializedSize <= int.MaxValue)
        {
            validatedSerializedSize =
                checked((int)serializedSize);
        }

        if (serializedPointer == IntPtr.Zero ||
            serializedSize != expectedSerializedBytes)
        {
            throw new InvalidOperationException(
                "The serialized SQLite snapshot size did not match its page attestation.");
        }

        var serializedDatabase =
            new byte[validatedSerializedSize];
        Marshal.Copy(
            serializedPointer,
            serializedDatabase,
            startIndex: 0,
            serializedDatabase.Length);
        if (serializedDatabase.Length < 100)
        {
            throw new InvalidOperationException(
                "The serialized SQLite snapshot header is incomplete.");
        }

        // BackupDatabase has already merged authoritative WAL pages into this
        // image. Normalize the standalone header to the legacy rollback
        // format so the committed database never requires a WAL sidecar.
        serializedDatabase[18] = 1;
        serializedDatabase[19] = 1;
        return serializedDatabase;
    }
    finally
    {
        if (serializedPointer != IntPtr.Zero)
        {
            try
            {
                if (validatedSerializedSize > 0)
                {
                    ZeroUnmanagedBuffer(
                        serializedPointer,
                        validatedSerializedSize);
                }
            }
            finally
            {
                SQLitePCL.raw.sqlite3_free(serializedPointer);
            }
        }
    }
}

static void ZeroUnmanagedBuffer(
    IntPtr buffer,
    int length)
{
    const int chunkSize = 64 * 1024;
    var zeros = new byte[Math.Min(length, chunkSize)];
    var offset = 0;
    while (offset < length)
    {
        var count = Math.Min(zeros.Length, length - offset);
        Marshal.Copy(
            zeros,
            startIndex: 0,
            IntPtr.Add(buffer, offset),
            count);
        offset += count;
    }
}

static string VerifySerializedSqliteSnapshot(
    byte[] serializedDatabase)
{
    const int sqliteDeserializeReadonly = 4;
    if (serializedDatabase.Length < 100 ||
        serializedDatabase[18] != 1 ||
        serializedDatabase[19] != 1)
    {
        throw new InvalidOperationException(
            "The serialized SQLite snapshot is not a rollback-journal database image.");
    }

    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = ":memory:",
        Mode = SqliteOpenMode.Memory,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString();
    var connection = new SqliteConnection(connectionString);
    var nativeBuffer = IntPtr.Zero;
    var nativeBufferLength = 0;
    var bufferMayBeInUse = false;
    var connectionClosed = false;
    try
    {
        connection.Open();
        nativeBufferLength =
            checked(serializedDatabase.Length + 20);
        nativeBuffer =
            SQLitePCL.raw.sqlite3_malloc64(nativeBufferLength);
        if (nativeBuffer == IntPtr.Zero)
        {
            throw new OutOfMemoryException(
                "SQLite could not allocate the serialized snapshot verification buffer.");
        }
        Marshal.Copy(
            serializedDatabase,
            startIndex: 0,
            nativeBuffer,
            serializedDatabase.Length);
        for (var offset = serializedDatabase.Length;
             offset < nativeBufferLength;
             offset++)
        {
            Marshal.WriteByte(nativeBuffer, offset, 0);
        }

        // Retain native ownership. If Close fails after deserialize, keep the
        // backing memory valid rather than risking a use-after-free. The
        // short-lived snapshot command then fails closed and leaks safely.
        bufferMayBeInUse = true;
        var result = SQLitePCL.raw.sqlite3_deserialize(
            connection.Handle!,
            "main",
            nativeBuffer,
            serializedDatabase.LongLength,
            nativeBufferLength,
            sqliteDeserializeReadonly);
        if (result != 0)
        {
            bufferMayBeInUse = false;
            throw new InvalidOperationException(
                "The serialized SQLite snapshot could not be opened read-only.");
        }
        using (var queryOnly = connection.CreateCommand())
        {
            queryOnly.CommandText = "PRAGMA query_only=ON;";
            queryOnly.ExecuteNonQuery();
        }

        var quickCheck =
            ExecuteScalarText(connection, "PRAGMA quick_check;");
        if (!string.Equals(
                quickCheck,
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The serialized SQLite snapshot failed read-only quick_check.");
        }
        return quickCheck;
    }
    finally
    {
        try
        {
            connection.Close();
            connectionClosed = true;
            bufferMayBeInUse = false;
        }
        finally
        {
            if (nativeBuffer != IntPtr.Zero &&
                (connectionClosed || !bufferMayBeInUse))
            {
                try
                {
                    ZeroUnmanagedBuffer(
                        nativeBuffer,
                        nativeBufferLength);
                }
                finally
                {
                    SQLitePCL.raw.sqlite3_free(nativeBuffer);
                }
            }

            if (connectionClosed)
            {
                connection.Dispose();
            }
        }
    }
}

static void AssertSerializedSnapshotFingerprint(
    byte[] serializedDatabase,
    SnapshotTargetFingerprint fingerprint)
{
    var sha256 = Convert.ToHexString(
        SHA256.HashData(serializedDatabase));
    if (fingerprint.Length != serializedDatabase.LongLength ||
        !string.Equals(
            fingerprint.Sha256,
            sha256,
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "The persisted temporary SQLite snapshot did not match its verified memory image.");
    }
}

static int PrintReadOnlyDatabaseSummary(string databasePath)
{
    try
    {
        using var identityLease =
            IsolatedPreparationDatabaseLease.AcquireReadOnlyDatabase(
                databasePath);
        var fullPath = identityLease.DatabasePath
            ?? throw new InvalidOperationException(
                "The read-only database identity lease did not resolve a database path.");
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"database_not_found={fullPath}");
            return 2;
        }

        var sidecarPaths = new[]
        {
            fullPath + "-wal",
            fullPath + "-shm",
            fullPath + "-journal"
        };
        if (sidecarPaths.Any(File.Exists))
        {
            throw new InvalidOperationException(
                "read-only-summary requires a standalone SQLite snapshot without WAL/SHM/journal sidecars.");
        }

        using var snapshotLease = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var snapshotLength = snapshotLease.Length;
        var snapshotLastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
        if (sidecarPaths.Any(File.Exists))
        {
            throw new InvalidOperationException(
                "read-only-summary detected a SQLite sidecar while acquiring the snapshot lease.");
        }

        var immutableDataSource = new Uri(fullPath).AbsoluteUri + "?immutable=1";
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = immutableDataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        if (sidecarPaths.Any(File.Exists))
        {
            throw new InvalidOperationException(
                "read-only-summary refused a SQLite provider that created a sidecar file.");
        }
        using (var queryOnly = connection.CreateCommand())
        {
            queryOnly.CommandText = "PRAGMA query_only=ON;";
            queryOnly.ExecuteNonQuery();
        }

        var tables = ReadSchemaObjects(connection, "table")
            .Where(entry => !entry.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();
        var indexes = ReadSchemaObjects(connection, "index")
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();

        var tableSummaries = tables
            .Select(table => BuildTableSummary(connection, table.Name))
            .ToList();
        var summary = new
        {
            database = fullPath,
            fileLength = snapshotLength,
            quickCheck = ExecuteScalarText(connection, "PRAGMA quick_check;"),
            applicationId = ExecuteScalarInt64(connection, "PRAGMA application_id;"),
            userVersion = ExecuteScalarInt64(connection, "PRAGMA user_version;"),
            schemaVersion = ExecuteScalarInt64(connection, "PRAGMA schema_version;"),
            pageCount = ExecuteScalarInt64(connection, "PRAGMA page_count;"),
            freeListCount = ExecuteScalarInt64(connection, "PRAGMA freelist_count;"),
            schemaDigest = ComputeSchemaDigest(tables.Concat(indexes)),
            tables = tableSummaries,
            indexes = indexes.Select(index => new
            {
                index.Name,
                index.TableName,
                sqlDigest = ComputeTextDigest(index.Sql)
            })
        };

        if (sidecarPaths.Any(File.Exists) ||
            snapshotLease.Length != snapshotLength ||
            File.GetLastWriteTimeUtc(fullPath) != snapshotLastWriteUtc)
        {
            throw new InvalidOperationException(
                "read-only-summary rejected a database that changed while the summary was generated.");
        }

        identityLease.AssertStable();
        Console.WriteLine(JsonSerializer.Serialize(
            summary,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 1;
    }
}

static async Task<int> PrintReadOnlyIntegrityReportAsync(
    string databasePath,
    string tenantCode,
    string officeCode,
    bool includeDetails)
{
    try
    {
        if (!TenantScopeCatalog.TryNormalizeTenantCode(
                tenantCode,
                out var normalizedTenantCode))
        {
            Console.Error.WriteLine(
                "invalid_tenant_code=Use a canonical tenant code such as USENET_GROUP or ITWORLD.");
            return 2;
        }

        if (!OfficeCodeCatalog.TryNormalizeOfficeCode(
                officeCode,
                out var normalizedOfficeCode))
        {
            Console.Error.WriteLine(
                "invalid_office_code=Use a canonical office code such as USENET, YEONSU, or ITWORLD.");
            return 2;
        }

        if (!TenantScopeCatalog.TenantContainsOffice(
                normalizedTenantCode,
                normalizedOfficeCode))
        {
            Console.Error.WriteLine(
                "tenant_office_mismatch=The office does not belong to the requested tenant.");
            return 2;
        }

        using var inspectionGuard =
            ImmutableSqliteInspectionGuard.Acquire(databasePath);
        var connectionString = BuildImmutableInspectionConnectionString(
            inspectionGuard.DatabasePath);
        var inspectionOptions =
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .Options;

        object report;
        var inspectionDb = new LocalDbContext(inspectionOptions);
        try
        {
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "syncdiag-read-only-integrity",
                Role = DomainConstants.RoleAdmin,
                TenantCode = normalizedTenantCode,
                OfficeCode = normalizedOfficeCode,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var scan = await new DataIntegrityIssueService(inspectionDb)
                .ScanAsync(session);
            var details = includeDetails
                ? scan.Issues.Select(issue => new
                {
                    issue.Code,
                    issue.Title,
                    issue.Severity,
                    issue.Area,
                    issue.EntityType,
                    issue.EntityId,
                    issue.ProfileId,
                    issue.AssetId,
                    issue.CustomerName,
                    issue.ItemName,
                    issue.AssetDisplayName,
                    issue.OfficeCode,
                    issue.CurrentValue,
                    issue.ExpectedValue,
                    issue.Message,
                    issue.SuggestedAction,
                    directActionKind = issue.DirectActionKind.ToString(),
                    issue.RelatedEntityIds,
                    issue.ReviewInfo,
                    itemDuplicateComparison = issue.ItemDuplicateComparison is null
                        ? null
                        : new
                        {
                            candidateCount = issue.ItemDuplicateComparison.Candidates.Count,
                            issue.ItemDuplicateComparison.CanMerge,
                            issue.ItemDuplicateComparison.BlockingConflictFields,
                            issue.ItemDuplicateComparison.BlockingReasons,
                            issue.ItemDuplicateComparison.SummaryText,
                            issue.ItemDuplicateComparison.RecommendedCanonicalId,
                            issue.ItemDuplicateComparison.TotalReferenceCount,
                            issue.ItemDuplicateComparison.CurrentStockTotal,
                            issue.ItemDuplicateComparison.WarehouseStockTotal,
                            referenceBreakdown = new
                            {
                                invoiceLines = issue.ItemDuplicateComparison.Candidates.Sum(candidate => candidate.InvoiceLineCount),
                                invoiceLineSerials = issue.ItemDuplicateComparison.Candidates.Sum(candidate => candidate.InvoiceLineSerialCount),
                                rentalAssets = issue.ItemDuplicateComparison.Candidates.Sum(candidate => candidate.RentalAssetCount),
                                rentalBillingTemplates = issue.ItemDuplicateComparison.Candidates.Sum(candidate => candidate.RentalBillingTemplateCount),
                                transferLines = issue.ItemDuplicateComparison.Candidates.Sum(candidate => candidate.InventoryTransferLineCount),
                                inventoryMovements = issue.ItemDuplicateComparison.Candidates.Sum(candidate => candidate.InventoryMovementCount),
                                stockLayers = issue.ItemDuplicateComparison.Candidates.Sum(candidate => candidate.StockLayerCount),
                                serialLedgers = issue.ItemDuplicateComparison.Candidates.Sum(candidate => candidate.SerialLedgerCount),
                                warehouseStockRows = issue.ItemDuplicateComparison.Candidates.Sum(candidate => candidate.ItemWarehouseStockRowCount),
                                itemPriceGrades = issue.ItemDuplicateComparison.Candidates.Sum(candidate => candidate.ItemPriceGradeCount)
                            }
                        }
                }).ToList()
                : null;
            report = new
            {
                inspectionMode = "read_only",
                inspectionSource = "immutable_sidecar_free_database",
                tenantCode = normalizedTenantCode,
                officeCode = normalizedOfficeCode,
                scan.TotalIssueCount,
                scan.ActionRequiredIssueCount,
                scan.InformationalIssueCount,
                issueTypeCount = scan.Summaries.Count,
                summaries = scan.Summaries.Select(summary => new
                {
                    summary.Code,
                    summary.Title,
                    summary.Severity,
                    summary.Area,
                    summary.Count,
                    summary.HasDirectAction
                }).ToList(),
                details
            };
        }
        finally
        {
            try
            {
                await inspectionDb.Database.CloseConnectionAsync();
            }
            finally
            {
                await inspectionDb.DisposeAsync();
                SqliteConnection.ClearAllPools();
            }
        }

        inspectionGuard.AssertStableSidecarFree();
        Console.WriteLine(JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 1;
    }
}

static IReadOnlyList<SchemaObject> ReadSchemaObjects(SqliteConnection connection, string type)
{
    using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT name, tbl_name, COALESCE(sql, '')
        FROM sqlite_master
        WHERE type = $type
        ORDER BY name;
        """;
    command.Parameters.AddWithValue("$type", type);
    using var reader = command.ExecuteReader();
    var entries = new List<SchemaObject>();
    while (reader.Read())
    {
        entries.Add(new SchemaObject(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2)));
    }

    return entries;
}

static TableSummary BuildTableSummary(SqliteConnection connection, string tableName)
{
    var columns = ReadTableColumns(connection, tableName);
    if (columns.Count == 0)
        return new TableSummary(
            tableName,
            0,
            ComputeTextDigest(string.Empty),
            Array.Empty<ColumnSummary>());

    var quotedColumns = string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)));
    var primaryKeyColumns = columns
        .Where(column => column.PrimaryKeyOrder > 0)
        .OrderBy(column => column.PrimaryKeyOrder)
        .Select(column => QuoteIdentifier(column.Name))
        .ToList();
    var orderBy = primaryKeyColumns.Count > 0
        ? string.Join(", ", primaryKeyColumns)
        : string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)));

    using var command = connection.CreateCommand();
    command.CommandText =
        $"SELECT {quotedColumns} FROM {QuoteIdentifier(tableName)} ORDER BY {orderBy};";
    using var reader = command.ExecuteReader();
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var columnHashes = columns
        .Select(_ => IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        .ToList();
    long rowCount = 0;
    try
    {
        while (reader.Read())
        {
            rowCount++;
            AppendInt64(hash, reader.FieldCount);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = reader.GetValue(index);
                AppendValue(hash, value);
                AppendValue(columnHashes[index], value);
            }
        }

        var columnSummaries = columns
            .Select((column, index) => new ColumnSummary(
                column.Name,
                Convert.ToHexString(columnHashes[index].GetHashAndReset())))
            .ToList();
        return new TableSummary(
            tableName,
            rowCount,
            Convert.ToHexString(hash.GetHashAndReset()),
            columnSummaries);
    }
    finally
    {
        foreach (var columnHash in columnHashes)
            columnHash.Dispose();
    }
}

static IReadOnlyList<TableColumn> ReadTableColumns(
    SqliteConnection connection,
    string tableName)
{
    using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({QuoteSqlString(tableName)});";
    using var reader = command.ExecuteReader();
    var columns = new List<TableColumn>();
    while (reader.Read())
    {
        columns.Add(new TableColumn(
            reader.GetString(1),
            Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture)));
    }

    return columns;
}

static string ComputeSchemaDigest(IEnumerable<SchemaObject> schemaObjects)
{
    var canonical = string.Join(
        "\n",
        schemaObjects
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => $"{entry.Name}\u001f{entry.TableName}\u001f{entry.Sql}"));
    return ComputeTextDigest(canonical);
}

static string ComputeTextDigest(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

static void AppendValue(IncrementalHash hash, object value)
{
    switch (value)
    {
        case DBNull:
            hash.AppendData([0]);
            return;
        case long integer:
            hash.AppendData([1]);
            AppendInt64(hash, integer);
            return;
        case double real:
            hash.AppendData([2]);
            hash.AppendData(BitConverter.GetBytes(real));
            return;
        case string text:
            hash.AppendData([3]);
            AppendBytes(hash, Encoding.UTF8.GetBytes(text));
            return;
        case byte[] blob:
            hash.AppendData([4]);
            AppendBytes(hash, blob);
            return;
        default:
            hash.AppendData([5]);
            AppendBytes(
                hash,
                Encoding.UTF8.GetBytes(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
            return;
    }
}

static void AppendBytes(IncrementalHash hash, byte[] bytes)
{
    AppendInt64(hash, bytes.LongLength);
    hash.AppendData(bytes);
}

static void AppendInt64(IncrementalHash hash, long value)
    => hash.AppendData(BitConverter.GetBytes(value));

static long ExecuteScalarInt64(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
}

static string ExecuteScalarText(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
}

static string QuoteIdentifier(string value)
    => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

static string QuoteSqlString(string value)
    => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

internal sealed record SchemaObject(string Name, string TableName, string Sql);

internal sealed record TableColumn(string Name, int PrimaryKeyOrder);

internal sealed record TableSummary(
    string Name,
    long RowCount,
    string DataDigest,
    IReadOnlyList<ColumnSummary> Columns);

internal sealed record ColumnSummary(string Name, string DataDigest);

internal sealed record SqliteSnapshotPaths(
    string SourceDatabasePath,
    string TargetDatabasePath);

internal sealed record SnapshotTargetFingerprint(
    long Length,
    DateTime LastWriteTimeUtc,
    string Sha256);

internal sealed record SnapshotSourceFingerprint(
    SnapshotTargetFingerprint Database,
    IReadOnlyList<SnapshotSourceSidecarFingerprint> Sidecars);

internal sealed record SnapshotSourceSidecarFingerprint(
    string Suffix,
    long Length,
    DateTime LastWriteTimeUtc,
    string Sha256);

internal sealed record SqliteSnapshotResult(
    long TargetLength,
    string TargetSha256,
    string QuickCheck,
    int SidecarCount);

internal sealed class SnapshotTestPhaseLease : IDisposable
{
    private const string EnvironmentPrefix =
        "GEORAEPLAN_SNAPSHOT_TEST_";
    private const string PhaseEnvironmentKey =
        EnvironmentPrefix + "PHASE";
    private const string OptInEnvironmentKey =
        EnvironmentPrefix + "FAULT_INJECTION";
    private const string RootEnvironmentKey =
        EnvironmentPrefix + "ROOT";
    private static readonly HashSet<string> AllowedEnvironmentKeys =
        new(StringComparer.Ordinal)
        {
            PhaseEnvironmentKey,
            OptInEnvironmentKey,
            RootEnvironmentKey
        };
    private static readonly HashSet<string> AllowedPhases =
        new(StringComparer.Ordinal)
        {
            "POST_SOURCE_BACKUP",
            "POST_QUICK_CHECK",
            "POST_COMMITTED_FINGERPRINT",
            "POST_RENAME_STATE_TRANSITION",
            "POST_RENAME_PERSISTENT_VALIDATION_FAILURE"
        };

    private readonly string _phase;
    private readonly string _root;
    private readonly string _signalPath;
    private readonly string _continuePath;
    private bool _disposed;

    private SnapshotTestPhaseLease(
        string phase,
        string root)
    {
        _phase = phase;
        _root = root;
        var markerStem =
            ".georaeplan-snapshot-test-" +
            phase.ToLowerInvariant().Replace('_', '-');
        _signalPath = Path.Combine(root, markerStem + ".signal");
        _continuePath = Path.Combine(root, markerStem + ".continue");
        if (File.Exists(_signalPath) ||
            Directory.Exists(_signalPath) ||
            File.Exists(_continuePath) ||
            Directory.Exists(_continuePath))
        {
            throw new InvalidOperationException(
                "Snapshot test phase markers must not already exist.");
        }
    }

    public static SnapshotTestPhaseLease? Acquire(
        string targetDatabasePath)
    {
        var configuredKeys = Environment
            .GetEnvironmentVariables()
            .Keys
            .OfType<string>()
            .Where(value => value.StartsWith(
                EnvironmentPrefix,
                StringComparison.Ordinal))
            .ToList();
        if (configuredKeys.Count == 0)
            return null;

#if !DEBUG
        throw new InvalidOperationException(
            "Snapshot test phase hooks are disabled in Release builds.");
#else
        if (configuredKeys.Any(value =>
                !AllowedEnvironmentKeys.Contains(value)))
        {
            throw new InvalidOperationException(
                "Snapshot test phase hooks reject unknown or legacy configuration.");
        }
        if (!IsTruthy(
                Environment.GetEnvironmentVariable(
                    "GEORAEPLAN_TEST_MODE")) ||
            !IsTruthy(
                Environment.GetEnvironmentVariable(
                    "GEORAEPLAN_TEST_SEED_MODE")) ||
            !IsTruthy(
                Environment.GetEnvironmentVariable(
                    OptInEnvironmentKey)))
        {
            throw new InvalidOperationException(
                "Snapshot test phase hooks require explicit isolated fault-injection authorization.");
        }

        var phase = Environment.GetEnvironmentVariable(
            PhaseEnvironmentKey)?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(phase) ||
            !AllowedPhases.Contains(phase))
        {
            throw new InvalidOperationException(
                "Snapshot test phase hook selection is invalid.");
        }
        var rootValue = Environment.GetEnvironmentVariable(
            RootEnvironmentKey);
        if (string.IsNullOrWhiteSpace(rootValue) ||
            !Path.IsPathFullyQualified(rootValue))
        {
            throw new InvalidOperationException(
                "Snapshot test phase hooks require an explicit absolute root.");
        }

        var expectedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                Path.GetDirectoryName(targetDatabasePath)
                ?? throw new InvalidOperationException(
                    "The snapshot target must have a parent directory.")));
        var configuredRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootValue));
        if (!string.Equals(
                configuredRoot,
                expectedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The snapshot test phase root must exactly match the validated target data directory.");
        }

        var directoryLease =
            SnapshotTestDirectoryLease.Acquire(configuredRoot);
        using (directoryLease)
            return new SnapshotTestPhaseLease(phase, configuredRoot);
#endif
    }

    public void Wait(string phase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(_phase, phase, StringComparison.Ordinal))
            return;

        using var directoryLease =
            SnapshotTestDirectoryLease.Acquire(_root);
        directoryLease.AssertStable();
        using (var signal = new FileStream(
                   _signalPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read))
        {
            signal.WriteByte(1);
            signal.Flush(flushToDisk: true);
        }
        directoryLease.AssertStable();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(_continuePath))
        {
            directoryLease.AssertStable();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The snapshot test phase hook timed out.");
            }
            Thread.Sleep(10);
        }

        var continueAttributes = File.GetAttributes(_continuePath);
        if ((continueAttributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                "The snapshot test continue marker must be a regular file.");
        }
        directoryLease.AssertStable();
    }

    public void InjectFailure(string phase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(_phase, phase, StringComparison.Ordinal))
            return;

        Wait(phase);
        throw new InvalidOperationException(
            "The snapshot test phase injected a post-rename validation failure.");
    }

    public bool InjectPersistentValidationFailure(string phase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(_phase, phase, StringComparison.Ordinal))
            return false;

        Wait(phase);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
    }

    private static bool IsTruthy(string? value)
        => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
}

internal sealed class SnapshotTestDirectoryLease : IDisposable
{
    private readonly string _path;
    private readonly SafeFileHandle _handle;
    private readonly DirectoryIdentity _identity;
    private readonly string _finalPath;
    private bool _disposed;

    private SnapshotTestDirectoryLease(
        string path,
        SafeFileHandle handle)
    {
        _path = path;
        _handle = handle;
        var information = ReadInformation(handle);
        _identity = ToIdentity(information);
        _finalPath = ReadFinalPath(handle);
        AssertInformation(information, _finalPath);
    }

    public static SnapshotTestDirectoryLease Acquire(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
        AssertNoReparseAncestors(fullPath);
        var handle = Open(fullPath);
        try
        {
            return new SnapshotTestDirectoryLease(fullPath, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void AssertStable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertNoReparseAncestors(_path);
        AssertInformation(
            ReadInformation(_handle),
            ReadFinalPath(_handle));
        using var pathHandle = Open(_path);
        var pathInformation = ReadInformation(pathHandle);
        var pathFinalPath = ReadFinalPath(pathHandle);
        if (ToIdentity(pathInformation) != _identity ||
            !string.Equals(
                pathFinalPath,
                _finalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The snapshot test phase directory identity changed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handle.Dispose();
    }

    private void AssertInformation(
        NativeMethods.ByHandleFileInformation information,
        string finalPath)
    {
        if (ToIdentity(information) != _identity ||
            ((FileAttributes)information.FileAttributes &
             FileAttributes.Directory) == 0 ||
            ((FileAttributes)information.FileAttributes &
             FileAttributes.ReparsePoint) != 0 ||
            !string.Equals(
                finalPath,
                _finalPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                finalPath,
                _path,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The snapshot test phase directory is unsafe or changed.");
        }
    }

    private static SafeFileHandle Open(string path)
    {
        var handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.FileReadAttributes,
            NativeMethods.FileShareRead |
            NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics |
            NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                "Could not acquire the snapshot test directory lease.");
        }
        return handle;
    }

    private static void AssertNoReparseAncestors(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (!current.Exists ||
                (current.Attributes &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The snapshot test phase directory cannot traverse a reparse point.");
            }
            current = current.Parent;
        }
    }

    private static DirectoryIdentity ToIdentity(
        NativeMethods.ByHandleFileInformation information)
        => new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) |
            information.FileIndexLow);

    private static NativeMethods.ByHandleFileInformation ReadInformation(
        SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(
                handle,
                out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not inspect the snapshot test directory identity.");
        }
        return information;
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = NativeMethods.GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                flags: 0);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not resolve the snapshot test directory path.");
            }
            if (length < buffer.Capacity)
            {
                var value = buffer.ToString();
                return value.StartsWith(
                        @"\\?\UNC\",
                        StringComparison.OrdinalIgnoreCase)
                    ? @"\\" + value[8..]
                    : value.StartsWith(
                        @"\\?\",
                        StringComparison.OrdinalIgnoreCase)
                        ? value[4..]
                        : value;
            }
            capacity = checked((int)length + 1);
        }
    }

    private readonly record struct DirectoryIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex);

    private static class NativeMethods
    {
        public const uint FileReadAttributes = 0x00000080;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint OpenExisting = 3;
        public const uint FileFlagBackupSemantics = 0x02000000;
        public const uint FileFlagOpenReparsePoint = 0x00200000;

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}

internal sealed class SnapshotSidecarLeaseSet : IDisposable
{
    private readonly IReadOnlyList<SidecarLease> _leases;
    private readonly IReadOnlyList<string> _paths;
    private bool _disposed;

    private SnapshotSidecarLeaseSet(
        IReadOnlyList<SidecarLease> leases,
        IReadOnlyList<string> paths)
    {
        _leases = leases;
        _paths = paths;
        AssertStable();
    }

    public static bool HasAnyEntry(string databasePath)
        => GetPaths(databasePath)
            .Any(SnapshotSourceFileLease.PathEntryExists);

    public static SnapshotSidecarLeaseSet Acquire(string databasePath)
    {
        var paths = GetPaths(databasePath).ToList();
        var leases = new List<SidecarLease>();
        try
        {
            foreach (var path in paths)
            {
                if (!SnapshotSourceFileLease.PathEntryExists(path))
                    continue;
                leases.Add(new SidecarLease(
                    Path.GetExtension(path),
                    path,
                    SnapshotSourceFileLease.Acquire(
                        path,
                        allowWriteSharing: true)));
            }
            return new SnapshotSidecarLeaseSet(leases, paths);
        }
        catch
        {
            foreach (var lease in leases)
                lease.Lease.Dispose();
            throw;
        }
    }

    public IReadOnlyList<SnapshotSourceSidecarFingerprint>
        CaptureFingerprints()
    {
        AssertStable();
        return _leases
            .Select(value =>
            {
                var fingerprint = value.Lease.CaptureFingerprint();
                return new SnapshotSourceSidecarFingerprint(
                    value.Suffix,
                    fingerprint.Length,
                    fingerprint.LastWriteTimeUtc,
                    string.Equals(
                        value.Suffix,
                        ".db-shm",
                        StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : fingerprint.Sha256);
            })
            .OrderBy(value => value.Suffix, StringComparer.Ordinal)
            .ToList();
    }

    public void AssertStable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var lease in _leases)
            lease.Lease.AssertStable();
        foreach (var path in _paths)
        {
            var expected = _leases.Any(value =>
                string.Equals(
                    value.Path,
                    path,
                    StringComparison.OrdinalIgnoreCase));
            if (SnapshotSourceFileLease.PathEntryExists(path) != expected)
            {
                throw new InvalidOperationException(
                    "The source SQLite sidecar entry set changed.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var lease in _leases.Reverse())
            lease.Lease.Dispose();
    }

    private static IEnumerable<string> GetPaths(string databasePath)
    {
        yield return databasePath + "-wal";
        yield return databasePath + "-shm";
        yield return databasePath + "-journal";
    }

    private sealed record SidecarLease(
        string Suffix,
        string Path,
        SnapshotSourceFileLease Lease);
}

internal sealed class SnapshotSourceFileLease : IDisposable
{
    private string _path;
    private readonly FileStream _stream;
    private readonly SnapshotNativeFileIdentity _identity;
    private string _finalPath;
    private readonly bool _canRename;
    private readonly bool _ownsCreatedFile;
    private bool _moved;
    private bool _forceMovedValidationFailure;
    private bool _disposed;

    public SnapshotNativeFileIdentity Identity
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _identity;
        }
    }

    private SnapshotSourceFileLease(
        string path,
        FileStream stream,
        bool canRename = false,
        bool ownsCreatedFile = false)
    {
        _path = Path.GetFullPath(path);
        _stream = stream;
        _canRename = canRename;
        _ownsCreatedFile = ownsCreatedFile;
        var information = ReadInformation(
            stream.SafeFileHandle);
        _identity = ToIdentity(information);
        _finalPath = ReadFinalPath(stream.SafeFileHandle);
        AssertCanonicalInformation(information, _finalPath);
    }

    private SnapshotSourceFileLease(
        string path,
        FileStream stream,
        SnapshotNativeFileIdentity identity,
        string finalPath,
        bool canRename,
        bool ownsCreatedFile)
    {
        _path = path;
        _stream = stream;
        _identity = identity;
        _finalPath = finalPath;
        _canRename = canRename;
        _ownsCreatedFile = ownsCreatedFile;
    }

    public static SnapshotSourceFileLease Acquire(
        string path,
        bool allowWriteSharing)
    {
        var fullPath = Path.GetFullPath(path);
        AssertNoReparsePath(fullPath);
        var shareMode = allowWriteSharing
            ? FileShare.ReadWrite
            : FileShare.Read;
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            shareMode);
        try
        {
            return new SnapshotSourceFileLease(
                fullPath,
                stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static SnapshotSourceFileLease CreateOwned(
        string path)
    {
        var fullPath = Path.GetFullPath(path);
        AssertNoReparseDirectoryPath(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                "The owned SQLite snapshot must have a parent directory."));
        var handle = SnapshotNativeMethods.CreateFileW(
            fullPath,
            SnapshotNativeMethods.GenericRead |
            SnapshotNativeMethods.GenericWrite |
            SnapshotNativeMethods.Delete,
            SnapshotNativeMethods.FileShareRead,
            IntPtr.Zero,
            SnapshotNativeMethods.CreateNew,
            SnapshotNativeMethods.FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                "Could not create the protected SQLite snapshot.");
        }

        FileStream? stream = null;
        try
        {
            var information = ReadInformation(handle);
            var identity = ToIdentity(information);
            var finalPath = ReadFinalPath(handle);
            AssertCanonicalCreatedFile(
                fullPath,
                information,
                finalPath);

            // Keep the new file delete-pending while FileStream takes
            // ownership and the assignment-only lease constructor completes.
            // Any exception in that sequence closes the protected handle
            // without leaving a residue.
            SetDeleteDisposition(handle, deleteFile: true);
            stream = new FileStream(handle, FileAccess.ReadWrite);
            var lease = new SnapshotSourceFileLease(
                fullPath,
                stream,
                identity,
                finalPath,
                canRename: true,
                ownsCreatedFile: true);
            SetDeleteDisposition(handle, deleteFile: false);
            return lease;
        }
        catch
        {
            if (!handle.IsClosed && !handle.IsInvalid)
            {
                TrySetDeleteDisposition(
                    handle,
                    deleteFile: true);
            }
            if (stream is not null)
                stream.Dispose();
            else
                handle.Dispose();
            throw;
        }
    }

    private static void AssertCanonicalCreatedFile(
        string path,
        SnapshotNativeMethods.ByHandleFileInformation information,
        string finalPath)
    {
        if (information.NumberOfLinks != 1 ||
            ((FileAttributes)information.FileAttributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint)) != 0 ||
            !string.Equals(
                finalPath,
                path,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The created SQLite snapshot file identity is unsafe.");
        }
    }

    public void ReplaceContent(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ownsCreatedFile || _moved)
        {
            throw new InvalidOperationException(
                "Only an owned uncommitted SQLite snapshot can be written.");
        }
        AssertStable();
        _stream.Position = 0;
        _stream.SetLength(0);
        _stream.Write(content);
        _stream.SetLength(content.LongLength);
        _stream.Flush(flushToDisk: true);
        AssertStable();
    }

    public void DeleteOwnedFile()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ownsCreatedFile)
        {
            throw new InvalidOperationException(
                "Only an owned SQLite snapshot can be deleted.");
        }
        SetDeleteDisposition(
            _stream.SafeFileHandle,
            deleteFile: true);
        Dispose();
    }

    public void MoveTo(
        string destinationPath,
        Action? afterRenameStateTransition = null,
        Func<bool>? injectPersistentValidationFailure = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_canRename)
        {
            throw new InvalidOperationException(
                "The SQLite snapshot lease was not opened for a protected rename.");
        }

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        AssertNoReparseDirectoryPath(
            Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException(
                "The SQLite snapshot destination must have a parent directory."));
        AssertStable();
        if (PathEntryExists(fullDestinationPath))
        {
            throw new InvalidOperationException(
                "The SQLite snapshot destination appeared before rename.");
        }

        RenameByHandle(_stream.SafeFileHandle, fullDestinationPath);
        _path = fullDestinationPath;
        _moved = true;
        afterRenameStateTransition?.Invoke();
        var information = ReadInformation(_stream.SafeFileHandle);
        if (ToIdentity(information) != _identity ||
            information.NumberOfLinks != 1 ||
            ((FileAttributes)information.FileAttributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                "The protected SQLite snapshot identity changed during rename.");
        }
        _forceMovedValidationFailure =
            injectPersistentValidationFailure?.Invoke() == true;
        AssertMovedStable();
    }

    private static void RenameByHandle(
        SafeFileHandle handle,
        string destinationPath)
    {
        var fileNameBytes = Encoding.Unicode.GetBytes(destinationPath);
        var rootDirectoryOffset = IntPtr.Size;
        var fileNameLengthOffset =
            rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(int);
        var bufferSize = checked(
            fileNameOffset +
            fileNameBytes.Length +
            sizeof(char));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var index = 0; index < bufferSize; index++)
                Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteIntPtr(
                buffer,
                rootDirectoryOffset,
                IntPtr.Zero);
            Marshal.WriteInt32(
                buffer,
                fileNameLengthOffset,
                fileNameBytes.Length);
            Marshal.Copy(
                fileNameBytes,
                0,
                IntPtr.Add(buffer, fileNameOffset),
                fileNameBytes.Length);
            if (!SnapshotNativeMethods.SetFileInformationByHandle(
                    handle,
                    SnapshotNativeMethods.FileRenameInfo,
                    buffer,
                    (uint)bufferSize))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not atomically rename the protected SQLite snapshot.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool PathEntryExists(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var pathEntry = SnapshotNativeMethods.CreateFileW(
            fullPath,
            SnapshotNativeMethods.FileReadAttributes,
            SnapshotNativeMethods.FileShareRead |
            SnapshotNativeMethods.FileShareWrite |
            SnapshotNativeMethods.FileShareDelete,
            IntPtr.Zero,
            SnapshotNativeMethods.OpenExisting,
            SnapshotNativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (pathEntry.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3)
                return false;
            throw new Win32Exception(
                error,
                "Could not inspect the SQLite sidecar path entry.");
        }

        var information = ReadInformation(pathEntry);
        if (((FileAttributes)information.FileAttributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                "The SQLite sidecar must be regular and non-reparse.");
        }
        return true;
    }

    public static void AssertCanonicalSingleLinkPath(
        string path)
    {
        using var lease = Acquire(
            path,
            allowWriteSharing: false);
        lease.AssertStable();
    }

    public SnapshotTargetFingerprint CaptureFingerprint()
    {
        AssertStable();
        var originalPosition = _stream.Position;
        try
        {
            _stream.Position = 0;
            var sha256 = Convert.ToHexString(
                SHA256.HashData(_stream));
        var fingerprint = new SnapshotTargetFingerprint(
                _stream.Length,
                ReadLastWriteTimeUtc(
                    ReadInformation(_stream.SafeFileHandle)),
                sha256);
            AssertStable();
            return fingerprint;
        }
        finally
        {
            _stream.Position = originalPosition;
        }
    }

    public void AssertStable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_moved)
        {
            AssertMovedStable();
            return;
        }
        AssertNoReparsePath(_path);
        var information = ReadInformation(
            _stream.SafeFileHandle);
        var finalPath = ReadFinalPath(
            _stream.SafeFileHandle);
        AssertCanonicalInformation(information, finalPath);

        using var pathProbe = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var pathInformation = ReadInformation(
            pathProbe.SafeFileHandle);
        var pathFinalPath = ReadFinalPath(
            pathProbe.SafeFileHandle);
        if (ToIdentity(pathInformation) != _identity ||
            !string.Equals(
                pathFinalPath,
                _finalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The source SQLite path identity changed.");
        }
    }

    private void AssertMovedStable()
    {
        if (_forceMovedValidationFailure)
        {
            throw new InvalidOperationException(
                "The snapshot test phase injected a persistent moved validation failure.");
        }
        AssertNoReparseDirectoryPath(
            Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException(
                "The moved SQLite snapshot must have a parent directory."));
        var information = ReadInformation(_stream.SafeFileHandle);
        if (ToIdentity(information) != _identity ||
            information.NumberOfLinks != 1 ||
            ((FileAttributes)information.FileAttributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                "The moved SQLite snapshot handle identity changed.");
        }

        using var pathProbe = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var pathInformation = ReadInformation(pathProbe.SafeFileHandle);
        var pathFinalPath = ReadFinalPath(pathProbe.SafeFileHandle);
        if (ToIdentity(pathInformation) != _identity ||
            pathInformation.NumberOfLinks != 1 ||
            ((FileAttributes)pathInformation.FileAttributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint)) != 0 ||
            !string.Equals(
                pathFinalPath,
                _path,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The moved SQLite snapshot path identity changed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream.Dispose();
    }

    private void AssertCanonicalInformation(
        SnapshotNativeMethods.ByHandleFileInformation information,
        string finalPath)
    {
        if (ToIdentity(information) != _identity ||
            information.NumberOfLinks != 1 ||
            ((FileAttributes)information.FileAttributes &
                (FileAttributes.Directory |
                 FileAttributes.ReparsePoint)) != 0 ||
            !string.Equals(
                finalPath,
                _finalPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                finalPath,
                _path,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The source SQLite file identity is unsafe or changed.");
        }
    }

    private static void AssertNoReparsePath(string path)
    {
        using var pathEntry = SnapshotNativeMethods.CreateFileW(
            path,
            SnapshotNativeMethods.FileReadAttributes,
            SnapshotNativeMethods.FileShareRead |
            SnapshotNativeMethods.FileShareWrite |
            SnapshotNativeMethods.FileShareDelete,
            IntPtr.Zero,
            SnapshotNativeMethods.OpenExisting,
            SnapshotNativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (pathEntry.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not inspect the SQLite snapshot path entry.");
        }
        var pathEntryInformation = ReadInformation(pathEntry);
        if (((FileAttributes)pathEntryInformation.FileAttributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                "The SQLite snapshot file must be regular and non-reparse.");
        }

        var current = new DirectoryInfo(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "The SQLite snapshot file must have a parent."));
        while (current is not null)
        {
            if (!current.Exists ||
                (current.Attributes &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The SQLite snapshot path cannot traverse a reparse point.");
            }
            current = current.Parent;
        }
    }

    private static void AssertNoReparseDirectoryPath(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (!current.Exists ||
                (current.Attributes &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The SQLite snapshot destination cannot traverse a reparse point.");
            }
            current = current.Parent;
        }
    }

    private static SnapshotNativeFileIdentity ToIdentity(
        SnapshotNativeMethods.ByHandleFileInformation information)
        => new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) |
            information.FileIndexLow);

    private static SnapshotNativeMethods.ByHandleFileInformation
        ReadInformation(SafeFileHandle handle)
    {
        if (!SnapshotNativeMethods.GetFileInformationByHandle(
                handle,
                out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not inspect the SQLite snapshot file identity.");
        }
        return information;
    }

    private static void SetDeleteDisposition(
        SafeFileHandle handle,
        bool deleteFile)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(
                buffer,
                0,
                deleteFile ? (byte)1 : (byte)0);
            if (!SnapshotNativeMethods.SetFileInformationByHandle(
                    handle,
                    SnapshotNativeMethods.FileDispositionInfo,
                    buffer,
                    bufferSize: 1))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not update the owned SQLite snapshot delete disposition.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void TrySetDeleteDisposition(
        SafeFileHandle handle,
        bool deleteFile)
    {
        try
        {
            SetDeleteDisposition(handle, deleteFile);
        }
        catch
        {
            // Best-effort cleanup remains bound to the exact created handle.
        }
    }

    private static DateTime ReadLastWriteTimeUtc(
        SnapshotNativeMethods.ByHandleFileInformation information)
    {
        var fileTime =
            ((long)information.LastWriteTimeHigh << 32) |
            information.LastWriteTimeLow;
        return DateTime.FromFileTimeUtc(fileTime);
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length =
                SnapshotNativeMethods.GetFinalPathNameByHandleW(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    flags: 0);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not resolve the SQLite snapshot file path.");
            }
            if (length < buffer.Capacity)
            {
                var path = buffer.ToString();
                return path.StartsWith(
                        @"\\?\UNC\",
                        StringComparison.OrdinalIgnoreCase)
                    ? @"\\" + path[8..]
                    : path.StartsWith(
                        @"\\?\",
                        StringComparison.OrdinalIgnoreCase)
                        ? path[4..]
                        : path;
            }
            capacity = checked((int)length + 1);
        }
    }

    private static class SnapshotNativeMethods
    {
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint Delete = 0x00010000;
        public const uint FileReadAttributes = 0x00000080;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint FileShareDelete = 0x00000004;
        public const uint OpenExisting = 3;
        public const uint CreateNew = 1;
        public const uint FileAttributeNormal = 0x00000080;
        public const uint FileFlagOpenReparsePoint = 0x00200000;
        public const int FileRenameInfo = 3;
        public const int FileDispositionInfo = 4;

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            IntPtr fileInformation,
            uint bufferSize);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}

internal readonly record struct SnapshotNativeFileIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex);

internal sealed class IsolatedTestServerTargetGuard : IDisposable
{
    private readonly IsolatedPreparationDatabaseLease _serverLease;
    private readonly FileStream _markerLease;
    private readonly string _markerSha256;
    private readonly long _markerLength;
    private bool _disposed;

    private IsolatedTestServerTargetGuard(
        IsolatedPreparationDatabaseLease serverLease,
        FileStream markerLease,
        string markerText)
    {
        _serverLease = serverLease;
        _markerLease = markerLease;
        _markerLength = markerLease.Length;
        _markerSha256 = ComputeSha256(markerText);
    }

    public static IsolatedTestServerTargetGuard Acquire(
        string serverRoot,
        string markerPath)
    {
        var markerLease = new FileStream(
            markerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        try
        {
            var markerText = ReadMarker(markerLease);
            var normalizedMarkerRoot =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(markerText.Trim()));
            var normalizedServerRoot =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(serverRoot));
            if (!string.Equals(
                    normalizedMarkerRoot,
                    normalizedServerRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The isolated server root attestation marker is mismatched.");
            }

            var serverLease =
                IsolatedPreparationDatabaseLease.AcquireForServerRoot(
                    normalizedServerRoot);
            return new IsolatedTestServerTargetGuard(
                serverLease,
                markerLease,
                markerText);
        }
        catch
        {
            markerLease.Dispose();
            throw;
        }
    }

    public void AssertStable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _serverLease.AssertStable();
        if (_markerLease.Length != _markerLength ||
            !string.Equals(
                _markerSha256,
                ComputeSha256(ReadMarker(_markerLease)),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The isolated server root attestation changed during sync.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _serverLease.Dispose();
        _markerLease.Dispose();
    }

    private static string ReadMarker(FileStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        var text = reader.ReadToEnd();
        stream.Position = 0;
        return text;
    }

    private static string ComputeSha256(string value)
        => Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(value)));
}
