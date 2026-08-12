using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InventoryTransferConflictEvidenceIntegrityReportTests
{
    [Fact]
    public async Task BuildIntegrityReportAsync_CountsOnlyUnsafeOrMissingEvidenceForSelectedBusinessDatabase()
    {
        var previousAppRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        var appRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-conflict-evidence-integrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", appRoot);

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var session = CreateUsenetAdminSession();
            var currentBusinessDatabaseName = session.SelectedBusinessDatabaseName;
            var otherBusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                TenantScopeCatalog.Itworld);
            Assert.False(string.Equals(
                currentBusinessDatabaseName,
                otherBusinessDatabaseName,
                StringComparison.OrdinalIgnoreCase));

            var safeExistingPath = Path.Combine(
                AppPaths.InventoryTransferConflictEvidenceDir,
                "safe-existing.pdf");
            var safeMissingPath = Path.Combine(
                AppPaths.InventoryTransferConflictEvidenceDir,
                "missing-current.pdf");
            var safeReplacementPath = Path.Combine(
                AppPaths.InventoryTransferConflictEvidenceDir,
                "safe-replacement.pdf");
            var otherMissingPath = Path.Combine(
                AppPaths.InventoryTransferConflictEvidenceDir,
                "missing-other-business.pdf");
            var unsafeDirectory = Path.Combine(appRoot, "outside-transactions");
            var unsafeCurrentPath = Path.Combine(
                unsafeDirectory,
                "unsafe-current.pdf");
            var unsafeOtherPath = Path.Combine(
                unsafeDirectory,
                "unsafe-other-business.pdf");

            Directory.CreateDirectory(unsafeDirectory);
            await File.WriteAllTextAsync(safeExistingPath, "safe-current");
            await File.WriteAllTextAsync(safeReplacementPath, "safe-replacement");
            await File.WriteAllTextAsync(unsafeCurrentPath, "unsafe-current");
            await File.WriteAllTextAsync(unsafeOtherPath, "unsafe-other");

            var currentUnsafeConflict = CreateConflict(
                currentBusinessDatabaseName,
                TenantScopeCatalog.UsenetGroup,
                unsafeCurrentPath);
            db.InventoryTransferTombstoneConflicts.AddRange(
                CreateConflict(
                    currentBusinessDatabaseName,
                    TenantScopeCatalog.UsenetGroup,
                    safeExistingPath),
                CreateConflict(
                    currentBusinessDatabaseName,
                    TenantScopeCatalog.UsenetGroup,
                    safeMissingPath),
                currentUnsafeConflict,
                CreateConflict(
                    otherBusinessDatabaseName,
                    TenantScopeCatalog.Itworld,
                    otherMissingPath),
                CreateConflict(
                    otherBusinessDatabaseName,
                    TenantScopeCatalog.Itworld,
                    unsafeOtherPath));
            await db.SaveChangesAsync();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);

            var report = await service.BuildIntegrityReportAsync(session);
            var issue = Assert.Single(
                report.Issues,
                candidate =>
                    candidate.Code ==
                    "missing_inventory_transfer_conflict_evidence_files");
            Assert.Equal(2, issue.Count);

            await File.WriteAllTextAsync(safeMissingPath, "restored-current");
            currentUnsafeConflict.ArchivedReceiveEvidencePath =
                safeReplacementPath;
            await db.SaveChangesAsync();

            var reportWithOnlyOtherBusinessDatabaseFailures =
                await service.BuildIntegrityReportAsync(session);
            Assert.DoesNotContain(
                reportWithOnlyOtherBusinessDatabaseFailures.Issues,
                candidate =>
                    candidate.Code ==
                    "missing_inventory_transfer_conflict_evidence_files");
            Assert.False(File.Exists(otherMissingPath));
            Assert.True(File.Exists(unsafeOtherPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                previousAppRoot);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(appRoot))
                Directory.Delete(appRoot, recursive: true);
        }
    }

    private static SessionState CreateUsenetAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "integrity-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        session.SetBusinessDatabase(
            TenantScopeCatalog.UsenetGroup,
            "USENET");
        return session;
    }

    private static LocalInventoryTransferTombstoneConflict CreateConflict(
        string businessDatabaseName,
        string tenantCode,
        string archivedReceiveEvidencePath)
    {
        var now = DateTime.UtcNow;
        return new LocalInventoryTransferTombstoneConflict
        {
            TransferId = Guid.NewGuid(),
            BusinessDatabaseName = businessDatabaseName,
            TenantCode = tenantCode,
            SourceOfficeCode = tenantCode == TenantScopeCatalog.Itworld
                ? OfficeCodeCatalog.Itworld
                : OfficeCodeCatalog.Usenet,
            TargetOfficeCode = tenantCode == TenantScopeCatalog.Itworld
                ? OfficeCodeCatalog.Itworld
                : OfficeCodeCatalog.Yeonsu,
            LocalSnapshotJson = "{}",
            ServerTombstoneJson = "{}",
            OutboxMutationIdsJson = "[]",
            ArchivedReceiveEvidencePath = archivedReceiveEvidencePath,
            LocalRevision = 1,
            ServerRevision = 2,
            ServerUpdatedAtUtc = now,
            Status = InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
            DetectedAtUtc = now,
            UpdatedAtUtc = now
        };
    }
}
