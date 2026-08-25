using GeoraePlan.Tools.SyncDiag;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedSeedRetryInvoiceVersionMetadataReconcilerTests
{
    [Fact]
    public async Task Reconcile_RebasesOnlyVersionMetadataAndRemovesOnlyStaleOutbox()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var db = new LocalDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync();

            var result = await IsolatedSeedRetryInvoiceVersionMetadataReconciler
                .ReconcileAsync(db, fixture.ServerDatabasePath);
            await transaction.CommitAsync();

            Assert.Equal(1, result.RebasedInvoices);
            Assert.Equal(1, result.RemovedStaleOutbox);

            db.ChangeTracker.Clear();
            var storedInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .Include(invoice => invoice.Lines)
                .SingleAsync(invoice => invoice.Id == fixture.InvoiceId);
            Assert.Equal(fixture.ServerVersionGroupId, storedInvoice.VersionGroupId);
            Assert.Equal(2, storedInvoice.VersionNumber);
            Assert.Equal(fixture.ServerPreviousVersionId, storedInvoice.PreviousVersionId);
            Assert.False(storedInvoice.IsLatestVersion);
            Assert.Equal(55, storedInvoice.Revision);
            Assert.True(storedInvoice.IsDirty);
            Assert.Equal("업무 내용 보존", storedInvoice.Memo);
            Assert.Equal(110_000m, storedInvoice.TotalAmount);
            Assert.Single(storedInvoice.Lines);
            Assert.Equal(
                "업무 품목 보존",
                storedInvoice.Lines.Single().ItemNameOriginal);

            var remainingOutbox = await db.SyncOutboxEntries
                .AsNoTracking()
                .OrderBy(entry => entry.Status)
                .ToListAsync();
            Assert.Equal(2, remainingOutbox.Count);
            Assert.Contains(
                remainingOutbox,
                entry =>
                    entry.EntityId == fixture.InvoiceId &&
                    entry.Status == "Acknowledged");
            Assert.Contains(
                remainingOutbox,
                entry => entry.EntityId == fixture.UnrelatedInvoiceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_FailsClosedWhenServerScopeDoesNotMatch()
    {
        var fixture = await CreateFixtureAsync(
            serverOfficeCode: OfficeCodeCatalog.Yeonsu);
        try
        {
            await using var db = new LocalDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => IsolatedSeedRetryInvoiceVersionMetadataReconciler
                    .ReconcileAsync(db, fixture.ServerDatabasePath));
            Assert.Contains(
                "scope or state contract",
                error.Message,
                StringComparison.Ordinal);
            await transaction.RollbackAsync();

            db.ChangeTracker.Clear();
            var storedInvoice = await db.Invoices
                .IgnoreQueryFilters()
                .SingleAsync(invoice => invoice.Id == fixture.InvoiceId);
            Assert.Equal(fixture.InvoiceId, storedInvoice.VersionGroupId);
            Assert.Equal(1, storedInvoice.VersionNumber);
            Assert.Null(storedInvoice.PreviousVersionId);
            Assert.True(storedInvoice.IsLatestVersion);
            Assert.Equal(7, storedInvoice.Revision);
            Assert.True(storedInvoice.IsDirty);
            Assert.Equal(
                1,
                await db.SyncOutboxEntries.CountAsync(entry =>
                    entry.EntityId == fixture.InvoiceId &&
                    entry.Status == "Failed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string? serverOfficeCode = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-isolated-invoice-metadata-retry-{Guid.NewGuid():N}");
        var appRoot = Path.Combine(root, "AppData");
        var serverRoot = Path.Combine(root, "Server");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(serverRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", appRoot);

        var customerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var invoiceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var unrelatedInvoiceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var serverVersionGroupId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var serverPreviousVersionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var nowUtc = new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc);

        await using (var db = new LocalDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "테스트 거래처",
                NameMatchKey = "테스트거래처",
                TradeType = CustomerTradeTypes.Sales,
                CreatedAtUtc = nowUtc.AddDays(-10),
                UpdatedAtUtc = nowUtc.AddDays(-10),
                Revision = 1
            });

            var invoice = new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "S-20260825-001",
                LocalTempNumber = "TMP-001",
                VersionGroupId = invoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                SourceWarehouseCode = DomainConstants.WarehouseUsenetMain,
                InvoiceDate = new DateOnly(2026, 8, 25),
                TotalAmount = 110_000m,
                SupplyAmount = 100_000m,
                VatAmount = 10_000m,
                VatMode = InvoiceVatModes.Included,
                Memo = "업무 내용 보존",
                CreatedAtUtc = nowUtc.AddDays(-2),
                UpdatedAtUtc = nowUtc,
                Revision = 7,
                IsDirty = true
            };
            invoice.Lines.Add(new LocalInvoiceLine
            {
                Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                InvoiceId = invoiceId,
                ItemNameOriginal = "업무 품목 보존",
                Unit = "EA",
                Quantity = 1,
                UnitPrice = 110_000m,
                LineAmount = 110_000m,
                ItemTrackingType = ItemTrackingTypes.NonStock
            });
            db.Invoices.Add(invoice);
            db.SyncOutboxEntries.AddRange(
                CreateOutbox(
                    invoiceId,
                    "Failed",
                    IsolatedSeedRetryInvoiceVersionMetadataReconciler.ConflictReason,
                    nowUtc),
                CreateOutbox(
                    invoiceId,
                    "Acknowledged",
                    string.Empty,
                    nowUtc.AddMinutes(-1)),
                CreateOutbox(
                    unrelatedInvoiceId,
                    "Failed",
                    "unrelated conflict",
                    nowUtc));
            await db.SaveChangesAsync();
        }

        var serverDatabasePath = Path.Combine(serverRoot, "거래플랜-local.db");
        await CreateServerDatabaseAsync(
            serverDatabasePath,
            invoiceId,
            customerId,
            serverOfficeCode ?? OfficeCodeCatalog.Usenet,
            serverVersionGroupId,
            serverPreviousVersionId);
        return new Fixture(
            root,
            serverDatabasePath,
            invoiceId,
            unrelatedInvoiceId,
            serverVersionGroupId,
            serverPreviousVersionId);
    }

    private static LocalSyncOutboxEntry CreateOutbox(
        Guid entityId,
        string status,
        string errorMessage,
        DateTime preparedAtUtc)
        => new()
        {
            Id = Guid.NewGuid(),
            MutationId = $"test:{entityId:N}:{status}",
            DeviceId = "TEST-DEVICE",
            EntityName = nameof(LocalInvoice),
            EntityId = entityId,
            ExpectedRevision = 7,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            Status = status,
            ErrorMessage = errorMessage,
            PreparedAtUtc = preparedAtUtc,
            AcknowledgedAtUtc = status == "Acknowledged"
                ? preparedAtUtc
                : null
        };

    private static async Task CreateServerDatabaseAsync(
        string databasePath,
        Guid invoiceId,
        Guid customerId,
        string officeCode,
        Guid versionGroupId,
        Guid previousVersionId)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE Invoices (
                Id TEXT NOT NULL PRIMARY KEY,
                CustomerId TEXT NOT NULL,
                TenantCode TEXT NOT NULL,
                OfficeCode TEXT NOT NULL,
                ResponsibleOfficeCode TEXT NOT NULL,
                VoucherType INTEGER NOT NULL,
                VersionGroupId TEXT NOT NULL,
                VersionNumber INTEGER NOT NULL,
                PreviousVersionId TEXT NULL,
                IsLatestVersion INTEGER NOT NULL,
                IsDeleted INTEGER NOT NULL,
                Revision INTEGER NOT NULL
            );
            INSERT INTO Invoices (
                Id, CustomerId, TenantCode, OfficeCode,
                ResponsibleOfficeCode, VoucherType, VersionGroupId,
                VersionNumber, PreviousVersionId, IsLatestVersion,
                IsDeleted, Revision)
            VALUES (
                $id, $customerId, $tenantCode, $officeCode,
                $responsibleOfficeCode, $voucherType, $versionGroupId,
                2, $previousVersionId, 0, 0, 55);
            """;
        command.Parameters.AddWithValue("$id", invoiceId.ToString());
        command.Parameters.AddWithValue("$customerId", customerId.ToString());
        command.Parameters.AddWithValue("$tenantCode", TenantScopeCatalog.UsenetGroup);
        command.Parameters.AddWithValue("$officeCode", officeCode);
        command.Parameters.AddWithValue("$responsibleOfficeCode", OfficeCodeCatalog.Usenet);
        command.Parameters.AddWithValue("$voucherType", (int)VoucherType.Sales);
        command.Parameters.AddWithValue("$versionGroupId", versionGroupId.ToString());
        command.Parameters.AddWithValue("$previousVersionId", previousVersionId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Fixture(
        string Root,
        string ServerDatabasePath,
        Guid InvoiceId,
        Guid UnrelatedInvoiceId,
        Guid ServerVersionGroupId,
        Guid ServerPreviousVersionId);
}
