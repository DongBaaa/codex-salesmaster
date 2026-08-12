using GeoraePlan.Tools.SyncDiag;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedSeedSyncFailureDiagnosticsTests
{
    [Fact]
    public void ServerAcceptedRevisionLines_EmitOnlyAllowedEntityAndCount()
    {
        var lines = TestSeedSyncConflictDiagnostics.BuildAcceptedRevisionLines(
        [
            new SyncAcceptedRevisionDto
            {
                EntityName = "RentalAssetAssignmentHistory",
                EntityId = Guid.NewGuid(),
                Revision = 2
            },
            new SyncAcceptedRevisionDto
            {
                EntityName = "RentalAssetAssignmentHistory",
                EntityId = Guid.NewGuid(),
                Revision = 3
            },
            new SyncAcceptedRevisionDto
            {
                EntityName = "unsafe/entity",
                EntityId = Guid.NewGuid(),
                Revision = 1
            }
        ]);
        var output = string.Join(Environment.NewLine, lines);

        Assert.Contains(
            "seed_sync_server_accepted_revision_group " +
            "entity=RentalAssetAssignmentHistory count=2",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "seed_sync_server_accepted_revision_group entity=unknown count=1",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe/entity", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerConflictLines_EmitOnlyAllowedEntityReasonAndCount()
    {
        const string secretCanary = "server-conflict-secret-canary";
        var lines = TestSeedSyncConflictDiagnostics.BuildLines(
        [
            new ConflictLogDto
            {
                EntityName = "Invoice",
                Reason =
                    "A paid, rental-linked, or versioned invoice cannot " +
                    $"be structurally changed with the same invoice id. {secretCanary}"
            },
            new ConflictLogDto
            {
                EntityName = "RentalAssetAssignmentHistory",
                Reason = $"Referenced customer is missing or deleted: {secretCanary}."
            },
            new ConflictLogDto
            {
                EntityName = "Payment",
                Reason =
                    $"Referenced invoice revision mismatch. client=1, server=2 {secretCanary}"
            },
            new ConflictLogDto
            {
                EntityName = $"unsafe/{secretCanary}",
                Reason = secretCanary
            }
        ]);
        var output = string.Join(Environment.NewLine, lines);

        Assert.Contains(
            "seed_sync_server_conflict_group entity=Invoice " +
            "reason_kind=protected_invoice_structure count=1",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "seed_sync_server_conflict_group " +
            "entity=RentalAssetAssignmentHistory " +
            "reason_kind=customer_reference_missing count=1",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "seed_sync_server_conflict_group entity=Payment " +
            "reason_kind=revision_conflict count=1",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "seed_sync_server_conflict_group entity=unknown " +
            "reason_kind=other count=1",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(secretCanary, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildLinesAsync_EmitsOnlyBoundedCategoriesAndNeverRawFailureData()
    {
        const string secretCanary = "seed-sync-secret-canary";
        var databasePath = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"seed-sync-failure-diagnostics-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.SyncOutboxEntries.AddRange(
                new LocalSyncOutboxEntry
                {
                    MutationId = Guid.NewGuid().ToString("N"),
                    DeviceId = "fixture-device",
                    EntityName = nameof(LocalPayment),
                    EntityId = Guid.NewGuid(),
                    TenantCode = "FIXTURE",
                    OfficeCode = "FIXTURE",
                    ResponsibleOfficeCode = "FIXTURE",
                    BusinessDatabaseName = "fixture.db",
                    SessionId = Guid.NewGuid(),
                    UserId = Guid.NewGuid(),
                    Status = "Failed",
                    ErrorMessage = $"REVISION_CONFLICT {secretCanary}"
                },
                new LocalSyncOutboxEntry
                {
                    MutationId = Guid.NewGuid().ToString("N"),
                    DeviceId = "fixture-device",
                    EntityName = $"unsafe/{secretCanary}",
                    EntityId = Guid.NewGuid(),
                    TenantCode = "FIXTURE",
                    OfficeCode = "FIXTURE",
                    ResponsibleOfficeCode = "FIXTURE",
                    BusinessDatabaseName = "fixture.db",
                    SessionId = Guid.NewGuid(),
                    UserId = Guid.NewGuid(),
                    Status = $"unsafe/{secretCanary}",
                    ErrorMessage = secretCanary
                },
                new LocalSyncOutboxEntry
                {
                    MutationId = Guid.NewGuid().ToString("N"),
                    DeviceId = "fixture-device",
                    EntityName = nameof(LocalInvoice),
                    EntityId = Guid.NewGuid(),
                    TenantCode = "FIXTURE",
                    OfficeCode = "FIXTURE",
                    ResponsibleOfficeCode = "FIXTURE",
                    BusinessDatabaseName = "fixture.db",
                    SessionId = Guid.NewGuid(),
                    UserId = Guid.NewGuid(),
                    Status = "Failed",
                    ErrorMessage =
                        "A paid, rental-linked, or versioned invoice cannot " +
                        $"be structurally changed with the same invoice id. {secretCanary}"
                },
                new LocalSyncOutboxEntry
                {
                    MutationId = Guid.NewGuid().ToString("N"),
                    DeviceId = "fixture-device",
                    EntityName = nameof(LocalRentalAssetAssignmentHistory),
                    EntityId = Guid.NewGuid(),
                    TenantCode = "FIXTURE",
                    OfficeCode = "FIXTURE",
                    ResponsibleOfficeCode = "FIXTURE",
                    BusinessDatabaseName = "fixture.db",
                    SessionId = Guid.NewGuid(),
                    UserId = Guid.NewGuid(),
                    Status = "Failed",
                    ErrorMessage =
                        $"Referenced customer is missing or deleted: {secretCanary}."
                });
            await db.SaveChangesAsync();

            var lines = await IsolatedSeedSyncFailureDiagnostics.BuildLinesAsync(db);
            var output = string.Join(Environment.NewLine, lines);

            Assert.Contains(
                "seed_sync_outbox_group entity=LocalPayment status=Failed " +
                "error_kind=revision_conflict count=1",
                output,
                StringComparison.Ordinal);
            Assert.Contains(
                "seed_sync_outbox_group entity=unknown status=unknown " +
                "error_kind=other count=1",
                output,
                StringComparison.Ordinal);
            Assert.Contains(
                "seed_sync_outbox_group entity=LocalInvoice status=Failed " +
                "error_kind=protected_invoice_structure count=1",
                output,
                StringComparison.Ordinal);
            Assert.Contains(
                "seed_sync_outbox_group " +
                "entity=LocalRentalAssetAssignmentHistory status=Failed " +
                "error_kind=customer_reference count=1",
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain(secretCanary, output, StringComparison.Ordinal);
            Assert.DoesNotContain("ErrorMessage", output, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }
}
