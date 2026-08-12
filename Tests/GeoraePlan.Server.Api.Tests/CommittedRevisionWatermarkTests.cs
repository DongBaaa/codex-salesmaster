using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class CommittedRevisionWatermarkTests
{
    [Fact]
    public async Task SeparateRevisionClocks_StillAllocateDatabaseWideMonotonicRevisions()
    {
        var databasePath = BuildDatabasePath();
        try
        {
            var currentUser = CreateAdminUser();
            var firstClock = new RevisionClock();
            var secondClock = new RevisionClock();
            var futureRevision = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
            firstClock.Initialize(futureRevision);
            secondClock.Initialize(futureRevision);

            await using (var firstContext = CreateDbContext(databasePath, currentUser, firstClock))
            {
                await firstContext.Database.EnsureCreatedAsync();
                var firstCustomer = CreateCustomer("FIRST-REVISION-CUSTOMER");
                firstContext.Customers.Add(firstCustomer);
                await firstContext.SaveChangesAsync();

                await using var secondContext = CreateDbContext(databasePath, currentUser, secondClock);
                var secondCustomer = CreateCustomer("SECOND-REVISION-CUSTOMER");
                secondContext.Customers.Add(secondCustomer);
                await secondContext.SaveChangesAsync();

                Assert.True(
                    secondCustomer.Revision > firstCustomer.Revision,
                    $"Expected a database-wide monotonic revision, but both contexts produced first={firstCustomer.Revision}, second={secondCustomer.Revision}.");
            }
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Pull_DoesNotAdvanceWatermarkPastAnUncommittedRevision()
    {
        var databasePath = BuildDatabasePath();
        try
        {
            var currentUser = CreateAdminUser();
            var sharedClock = new RevisionClock();

            await using var writerContext = CreateDbContext(databasePath, currentUser, sharedClock);
            await writerContext.Database.EnsureCreatedAsync();
            await writerContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

            await using var readerContext = CreateDbContext(databasePath, currentUser, sharedClock);
            var readerController = CreateSyncController(readerContext, currentUser, sharedClock);

            await using var transaction = await writerContext.Database.BeginTransactionAsync();
            var uncommittedCustomer = CreateCustomer("UNCOMMITTED-WATERMARK-CUSTOMER");
            writerContext.Customers.Add(uncommittedCustomer);
            await writerContext.SaveChangesAsync();

            var beforeCommitResponse = await readerController.Pull(0, CancellationToken.None);
            var beforeCommitOk = Assert.IsType<OkObjectResult>(beforeCommitResponse.Result);
            var beforeCommit = Assert.IsType<SyncPullResponse>(beforeCommitOk.Value);

            Assert.DoesNotContain(beforeCommit.Customers, customer => customer.Id == uncommittedCustomer.Id);
            Assert.Equal(0, beforeCommit.CurrentServerRevision);

            await transaction.CommitAsync();

            var afterCommitResponse = await readerController.Pull(0, CancellationToken.None);
            var afterCommitOk = Assert.IsType<OkObjectResult>(afterCommitResponse.Result);
            var afterCommit = Assert.IsType<SyncPullResponse>(afterCommitOk.Value);

            Assert.Contains(afterCommit.Customers, customer => customer.Id == uncommittedCustomer.Id);
            Assert.Equal(uncommittedCustomer.Revision, afterCommit.CurrentServerRevision);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task BusinessSchemaUpgrade_CreatesRevisionStateBeforeLegacyRepairSave()
    {
        var databasePath = BuildDatabasePath();
        try
        {
            var currentUser = CreateAdminUser();
            var revisionClock = new RevisionClock();
            await using var dbContext = CreateDbContext(databasePath, currentUser, revisionClock);
            await dbContext.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("LEGACY-SCHEMA-UPGRADE-CUSTOMER");
            var versionGroupId = Guid.NewGuid();
            var firstInvoice = CreateInvoice(
                customer.Id,
                versionGroupId,
                versionNumber: 1,
                invoiceNumber: "LEGACY-UPGRADE-001");
            var secondInvoice = CreateInvoice(
                customer.Id,
                versionGroupId,
                versionNumber: 2,
                invoiceNumber: "LEGACY-UPGRADE-002");
            dbContext.AddRange(customer, firstInvoice, secondInvoice);
            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();

            var futureRevision = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "Invoices"
                 SET "Revision" = {futureRevision}
                 WHERE "Id" = {firstInvoice.Id};
                 """);
            await dbContext.Database.ExecuteSqlRawAsync(
                """DROP TABLE "SyncRevisionStates";""");

            var method = typeof(DbInitializer).GetMethod(
                "EnsureBusinessDatabaseSchemaAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var task = method!.Invoke(
                null,
                new object?[] { dbContext, NullLogger.Instance, CancellationToken.None }) as Task;
            Assert.NotNull(task);
            await task!;

            Assert.True(await dbContext.SyncRevisionStates
                .AsNoTracking()
                .AnyAsync(state => state.Id == 1 && state.CurrentRevision > 0));

            // Invoice-version normalization now runs after the complete runtime
            // schema and scope backfills. Invoke that separated repair stage
            // explicitly so this regression still proves that the committed
            // revision state exists before the legacy repair SaveChanges call.
            var invoiceVersionRepairMethod = typeof(DbInitializer).GetMethod(
                "EnsureInvoiceVersionColumnsAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(invoiceVersionRepairMethod);
            var invoiceVersionRepairTask = invoiceVersionRepairMethod!.Invoke(
                null,
                new object?[] { dbContext, CancellationToken.None }) as Task;
            Assert.NotNull(invoiceVersionRepairTask);
            await invoiceVersionRepairTask!;

            var versions = await dbContext.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => invoice.VersionGroupId == versionGroupId)
                .OrderBy(invoice => invoice.VersionNumber)
                .ToListAsync();
            Assert.False(versions[0].IsLatestVersion);
            Assert.True(versions[0].Revision > futureRevision);
            Assert.True(versions[1].IsLatestVersion);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task PreRepairCutover_SeedsEveryPhysicalDatabaseToCommonExistingMaximum()
    {
        var centralDatabasePath = BuildDatabasePath();
        var tenantDatabasePath = BuildDatabasePath();
        try
        {
            var currentUser = CreateAdminUser();
            var futureCentralRevision = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
            var futureTenantRevision = futureCentralRevision + 10_000;

            await using var centralDbContext = CreateDbContext(
                centralDatabasePath,
                currentUser,
                new RevisionClock());
            await centralDbContext.Database.EnsureCreatedAsync();
            var centralCustomer = CreateCustomer("CENTRAL-CUTOVER-CUSTOMER");
            centralDbContext.Customers.Add(centralCustomer);
            await centralDbContext.SaveChangesAsync();
            await centralDbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "Customers"
                 SET "Revision" = {futureCentralRevision}
                 WHERE "Id" = {centralCustomer.Id};
                 """);
            await centralDbContext.Database.ExecuteSqlRawAsync(
                """DROP TABLE "SyncRevisionStates";""");

            await using (var tenantSeedContext = CreateDbContext(
                             tenantDatabasePath,
                             currentUser,
                             new RevisionClock()))
            {
                await tenantSeedContext.Database.EnsureCreatedAsync();
                var tenantCustomer = CreateCustomer("TENANT-CUTOVER-CUSTOMER");
                tenantSeedContext.Customers.Add(tenantCustomer);
                await tenantSeedContext.SaveChangesAsync();
                await tenantSeedContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE "Customers"
                     SET "Revision" = {futureTenantRevision}
                     WHERE "Id" = {tenantCustomer.Id};
                     """);
                await tenantSeedContext.Database.ExecuteSqlRawAsync(
                    """DROP TABLE "SyncRevisionStates";""");
            }

            var tenantConnection = new TenantDatabaseConnectionInfo
            {
                UseSqlite = true,
                ConnectionString = $"Data Source={tenantDatabasePath};Pooling=False;Default Timeout=5",
                TenantCode = TenantScopeCatalog.Itworld,
                IsDedicatedBusinessDatabase = true
            };
            var method = typeof(DbInitializer).GetMethod(
                "PrepareCommittedRevisionStatesBeforeRepairsAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var task = method!.Invoke(
                null,
                new object?[]
                {
                    centralDbContext,
                    new[] { tenantConnection },
                    CancellationToken.None
                }) as Task<long>;
            Assert.NotNull(task);
            var commonFloor = await task!;

            Assert.Equal(futureTenantRevision, commonFloor);
            Assert.Equal(futureTenantRevision, await centralDbContext.GetCommittedRevisionAsync());
            await using var tenantVerificationContext = CreateDbContext(
                tenantDatabasePath,
                currentUser,
                new RevisionClock());
            Assert.Equal(
                futureTenantRevision,
                await tenantVerificationContext.GetCommittedRevisionAsync());

            var postCutoverCentralCustomer = CreateCustomer("CENTRAL-POST-CUTOVER");
            centralDbContext.Customers.Add(postCutoverCentralCustomer);
            await centralDbContext.SaveChangesAsync();
            Assert.True(postCutoverCentralCustomer.Revision > futureTenantRevision);

            var postCutoverTenantCustomer = CreateCustomer("TENANT-POST-CUTOVER");
            tenantVerificationContext.Customers.Add(postCutoverTenantCustomer);
            await tenantVerificationContext.SaveChangesAsync();
            Assert.True(postCutoverTenantCustomer.Revision > futureTenantRevision);
        }
        finally
        {
            DeleteDatabaseFiles(centralDatabasePath);
            DeleteDatabaseFiles(tenantDatabasePath);
        }
    }

    [Fact]
    public async Task SaveChangesBooleanOverloads_UseCommittedRevisionAllocator()
    {
        var databasePath = BuildDatabasePath();
        try
        {
            var currentUser = CreateAdminUser();
            await using var dbContext = CreateDbContext(
                databasePath,
                currentUser,
                new RevisionClock());
            await dbContext.Database.EnsureCreatedAsync();

            var syncCustomer = CreateCustomer("SYNC-BOOLEAN-OVERLOAD");
            dbContext.Customers.Add(syncCustomer);
            dbContext.SaveChanges(acceptAllChangesOnSuccess: false);
            Assert.True(syncCustomer.Revision > 0);
            Assert.Equal(
                syncCustomer.Revision,
                await dbContext.GetCommittedRevisionAsync());
            dbContext.ChangeTracker.AcceptAllChanges();

            var asyncCustomer = CreateCustomer("ASYNC-BOOLEAN-OVERLOAD");
            dbContext.Customers.Add(asyncCustomer);
            await dbContext.SaveChangesAsync(
                acceptAllChangesOnSuccess: false,
                CancellationToken.None);
            Assert.True(asyncCustomer.Revision > syncCustomer.Revision);
            Assert.Equal(
                asyncCustomer.Revision,
                await dbContext.GetCommittedRevisionAsync());
            dbContext.ChangeTracker.AcceptAllChanges();
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task OuterTransactionRollback_RollsBackRevisionStateAndBusinessRowTogether()
    {
        var databasePath = BuildDatabasePath();
        try
        {
            var currentUser = CreateAdminUser();
            await using var writerContext = CreateDbContext(
                databasePath,
                currentUser,
                new RevisionClock());
            await writerContext.Database.EnsureCreatedAsync();

            var rolledBackCustomer = CreateCustomer("ROLLED-BACK-REVISION-CUSTOMER");
            await using (var transaction = await writerContext.Database.BeginTransactionAsync())
            {
                writerContext.Customers.Add(rolledBackCustomer);
                await writerContext.SaveChangesAsync();
                Assert.True(rolledBackCustomer.Revision > 0);
                await transaction.RollbackAsync();
            }

            await using var verificationContext = CreateDbContext(
                databasePath,
                currentUser,
                new RevisionClock());
            Assert.False(await verificationContext.Customers
                .IgnoreQueryFilters()
                .AnyAsync(customer => customer.Id == rolledBackCustomer.Id));
            Assert.Equal(0, await verificationContext.GetCommittedRevisionAsync());
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static AppDbContext CreateDbContext(
        string databasePath,
        TestCurrentUserContext currentUser,
        RevisionClock revisionClock)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False;Default Timeout=5")
            .Options;
        return new AppDbContext(options, currentUser, revisionClock);
    }

    private static SyncController CreateSyncController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser,
        RevisionClock revisionClock)
        => new(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            revisionClock,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalAssignmentHistoryService(dbContext),
            new RentalSettlementRecalculationService(dbContext));

    private static Customer CreateCustomer(string name)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name.Replace("-", string.Empty, StringComparison.Ordinal),
            TradeType = "Sales"
        };

    private static Invoice CreateInvoice(
        Guid customerId,
        Guid versionGroupId,
        int versionNumber,
        string invoiceNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = invoiceNumber,
            VersionGroupId = versionGroupId,
            VersionNumber = versionNumber,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 27)
        };

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "revision-watermark-admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

    private static string BuildDatabasePath()
        => Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-committed-revision-{Guid.NewGuid():N}.db");

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A failed assertion is more useful than a best-effort cleanup failure.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed assertion is more useful than a best-effort cleanup failure.
            }
        }
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(
            Guid customerId,
            DateOnly invoiceDate,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"{invoiceDate:yyyyMM}-REVISION");
    }

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(
            string area,
            string ownerId,
            Guid fileId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
            => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
