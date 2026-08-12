using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class SyncPushMutationSerializationTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"georaeplan-sync-mutation-serialization-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(CreateAdminUser());
        await dbContext.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentCustomerMutationPushes_AcceptOnceAndReturnOneDuplicate()
    {
        var customerId = Guid.NewGuid();
        var mutationId = $"concurrent-customer:{customerId:N}";
        var firstInsideSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttemptingPush = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingInterceptor = new BlockingFirstSaveInterceptor(firstInsideSave, releaseFirstSave);

        var firstUser = CreateAdminUser();
        var secondUser = CreateAdminUser();
        await using var firstDbContext = CreateDbContext(firstUser, blockingInterceptor);
        await using var secondDbContext = CreateDbContext(secondUser);
        var firstController = CreateController(firstDbContext, firstUser);
        var secondController = CreateController(secondDbContext, secondUser);
        using var startBarrier = new Barrier(2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var firstPush = Task.Run(async () =>
        {
            if (!startBarrier.SignalAndWait(TimeSpan.FromSeconds(5), timeout.Token))
                throw new TimeoutException("The concurrent Push start barrier timed out.");

            return await firstController.Push(
                CreateCustomerRequest(customerId, mutationId),
                timeout.Token);
        }, timeout.Token);

        var secondPush = Task.Run(async () =>
        {
            if (!startBarrier.SignalAndWait(TimeSpan.FromSeconds(5), timeout.Token))
                throw new TimeoutException("The concurrent Push start barrier timed out.");

            await firstInsideSave.Task.WaitAsync(timeout.Token);
            secondAttemptingPush.TrySetResult();
            return await secondController.Push(
                CreateCustomerRequest(customerId, mutationId),
                timeout.Token);
        }, timeout.Token);

        var secondCompletedBeforeFirstReleased = false;
        try
        {
            await firstInsideSave.Task.WaitAsync(timeout.Token);
            await secondAttemptingPush.Task.WaitAsync(timeout.Token);
            secondCompletedBeforeFirstReleased =
                await Task.WhenAny(secondPush, Task.Delay(200, timeout.Token)) == secondPush;
        }
        finally
        {
            releaseFirstSave.TrySetResult();
        }

        var responses = await Task.WhenAll(firstPush, secondPush).WaitAsync(timeout.Token);
        Assert.False(
            secondCompletedBeforeFirstReleased,
            "The second mutation Push must wait while the first Push owns the serialized mutation scope.");

        var results = responses.Select(AssertOk).ToList();
        Assert.All(results, result => Assert.Equal(0, result.ConflictCount));

        // Duplicate replays are successful accepts in the wire contract, so subtract them
        // to count newly applied mutations separately from acknowledged duplicates.
        Assert.Equal(1, results.Sum(result => result.AcceptedCount - result.DuplicateMutationCount));
        Assert.Equal(1, results.Sum(result => result.DuplicateMutationCount));
        Assert.Equal(2, results.Sum(result => result.AcceptedCount));

        await using var verificationDb = CreateDbContext(CreateAdminUser());
        Assert.Equal(
            1,
            await verificationDb.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(customer => customer.Id == customerId));
        Assert.Equal(
            1,
            await verificationDb.ProcessedSyncMutations
                .AsNoTracking()
                .CountAsync(receipt => receipt.MutationId == mutationId));
        Assert.Equal(
            1,
            await verificationDb.AuditLogs
                .AsNoTracking()
                .CountAsync(audit =>
                    audit.EntityName == nameof(Customer) &&
                    audit.EntityId == customerId.ToString()));
    }

    [Fact]
    public async Task ConcurrentLegacyCustomerPushes_WithoutMutationIds_AreSerializedAndReturnOk()
    {
        var customerId = Guid.NewGuid();
        var firstInsideSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttemptingPush = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingInterceptor = new BlockingFirstSaveInterceptor(firstInsideSave, releaseFirstSave);

        var firstUser = CreateAdminUser();
        var secondUser = CreateAdminUser();
        await using var firstDbContext = CreateDbContext(firstUser, blockingInterceptor);
        await using var secondDbContext = CreateDbContext(secondUser);
        var firstController = CreateController(firstDbContext, firstUser);
        var secondController = CreateController(secondDbContext, secondUser);
        using var startBarrier = new Barrier(2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var firstPush = Task.Run(async () =>
        {
            if (!startBarrier.SignalAndWait(TimeSpan.FromSeconds(5), timeout.Token))
                throw new TimeoutException("The concurrent legacy Push start barrier timed out.");

            return await firstController.Push(
                CreateLegacyCustomerRequest(customerId),
                timeout.Token);
        }, timeout.Token);

        var secondPush = Task.Run(async () =>
        {
            if (!startBarrier.SignalAndWait(TimeSpan.FromSeconds(5), timeout.Token))
                throw new TimeoutException("The concurrent legacy Push start barrier timed out.");

            await firstInsideSave.Task.WaitAsync(timeout.Token);
            secondAttemptingPush.TrySetResult();
            return await secondController.Push(
                CreateLegacyCustomerRequest(customerId),
                timeout.Token);
        }, timeout.Token);

        var secondCompletedBeforeFirstReleased = false;
        try
        {
            await firstInsideSave.Task.WaitAsync(timeout.Token);
            await secondAttemptingPush.Task.WaitAsync(timeout.Token);
            secondCompletedBeforeFirstReleased =
                await Task.WhenAny(secondPush, Task.Delay(200, timeout.Token)) == secondPush;
        }
        finally
        {
            releaseFirstSave.TrySetResult();
        }

        var responses = await Task.WhenAll(firstPush, secondPush).WaitAsync(timeout.Token);
        Assert.False(
            secondCompletedBeforeFirstReleased,
            "A legacy Push without MutationId must wait while another entity Push owns the serialized scope.");

        var results = responses.Select(AssertOk).ToList();
        Assert.Equal(1, results.Sum(result => result.AcceptedCount));
        Assert.Equal(1, results.Sum(result => result.ConflictCount));
        Assert.All(results, result => Assert.Equal(0, result.DuplicateMutationCount));

        await using var verificationDb = CreateDbContext(CreateAdminUser());
        Assert.Equal(
            1,
            await verificationDb.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(customer => customer.Id == customerId));
        Assert.Equal(0, await verificationDb.ProcessedSyncMutations.AsNoTracking().CountAsync());
        Assert.Equal(
            1,
            await verificationDb.AuditLogs
                .AsNoTracking()
                .CountAsync(audit =>
                    audit.EntityName == nameof(Customer) &&
                    audit.EntityId == customerId.ToString()));
    }

    private static SyncPushRequest CreateLegacyCustomerRequest(Guid customerId)
    {
        var request = CreateCustomerRequest(customerId, string.Empty);
        var customer = request.Customers[0];
        customer.CreatedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        customer.UpdatedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        customer.MutationCreatedAtUtc = null;
        return request;
    }

    private static SyncPushRequest CreateCustomerRequest(Guid customerId, string mutationId)
        => new()
        {
            DeviceId = "concurrent-customer-device",
            Customers =
            [
                new CustomerDto
                {
                    Id = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Concurrent serialized customer",
                    NameMatchKey = "CONCURRENTSERIALIZEDCUSTOMER",
                    TradeType = CustomerClassificationNormalizer.Sales,
                    CreatedAtUtc = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc),
                    ExpectedRevision = 0,
                    MutationId = mutationId,
                    MutationCreatedAtUtc = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

    private AppDbContext CreateDbContext(
        TestCurrentUserContext currentUser,
        SaveChangesInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Cache=Shared;Default Timeout=10;Pooling=False");
        if (interceptor is not null)
            optionsBuilder.AddInterceptors(interceptor);

        return new AppDbContext(optionsBuilder.Options, currentUser, new RevisionClock());
    }

    private static SyncController CreateController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new SyncController(
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
    }

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

    private static SyncPushResult AssertOk(ActionResult<SyncPushResult> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<SyncPushResult>(ok.Value);
    }

    private sealed class BlockingFirstSaveInterceptor(
        TaskCompletionSource entered,
        TaskCompletionSource release) : SaveChangesInterceptor
    {
        private int _blocked;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return result;
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
            => Task.FromResult($"INV-{invoiceDate:yyyyMMdd}-{customerId:N}");
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
