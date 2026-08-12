using System.Data.Common;
using System.Reflection;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class StoredFileReferenceReconcilerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"georaeplan-file-reconciler-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly TestCurrentUserContext _currentUser = new();
    private readonly RevisionClock _revisionClock = new();

    public StoredFileReferenceReconcilerTests()
    {
        _connection = new SqliteConnection($"Data Source={_databasePath}");
        _connection.Open();
        using var dbContext = CreateDbContext();
        dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task DeleteUnreferencedAsync_PreservesReferencesAcrossAllAttachmentTablesIncludingSoftDeletedRows()
    {
        const string contractPath = "store/customer-contract.pdf";
        const string transactionPath = "store/transaction-receipt.pdf";
        const string paymentPath = "store/payment-receipt.pdf";
        const string inventoryTransferPath = "store/inventory-transfer-receipt.pdf";
        const string orphanPath = "store/orphan.pdf";

        await using (var dbContext = CreateDbContext())
        {
            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                NameOriginal = "reconciler customer",
                NameMatchKey = "RECONCILERCUSTOMER",
                TradeType = "Sales"
            };
            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                InvoiceNumber = "RECONCILER-001"
            };
            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                Amount = 1m
            };
            var transaction = new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TransactionKind = "Receipt"
            };

            dbContext.AddRange(
                customer,
                invoice,
                payment,
                transaction,
                new CustomerContract
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customer.Id,
                    StoragePath = contractPath,
                    IsDeleted = true
                },
                new TransactionAttachment
                {
                    Id = Guid.NewGuid(),
                    TransactionId = transaction.Id,
                    StoragePath = transactionPath,
                    IsDeleted = true
                },
                new PaymentAttachment
                {
                    Id = Guid.NewGuid(),
                    PaymentId = payment.Id,
                    StoragePath = paymentPath,
                    IsDeleted = true
                },
                new InventoryTransfer
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Received,
                    ReceiveEvidencePath = inventoryTransferPath,
                    IsDeleted = true
                });
            await dbContext.SaveChangesAsync();
        }

        var storage = new RecordingCentralFileStorage();
        var reconciler = CreateReconciler(storage);

        await reconciler.DeleteUnreferencedAsync(
            [contractPath, transactionPath, paymentPath, inventoryTransferPath, orphanPath],
            CancellationToken.None);

        Assert.Equal([orphanPath], storage.DeletedPaths);
    }

    [Fact]
    public async Task DeleteUnreferencedAsync_WhenAnyIndependentLookupFails_DeletesNothing()
    {
        var storage = new RecordingCentralFileStorage();
        var missingDatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-file-reconciler-missing-{Guid.NewGuid():N}.db");
        var reconciler = CreateReconciler(
            storage,
            dedicatedConnections:
            [
                new TenantDatabaseConnectionInfo
                {
                    UseSqlite = true,
                    ConnectionString = $"Data Source={missingDatabasePath};Mode=ReadOnly",
                    TenantCode = TenantScopeCatalog.Itworld,
                    IsDedicatedBusinessDatabase = true
                }
            ]);

        await reconciler.DeleteUnreferencedAsync(
            ["store/first-orphan.pdf", "store/second-orphan.pdf"],
            CancellationToken.None);

        Assert.Empty(storage.DeletedPaths);
    }

    [Fact]
    public async Task DeleteUnreferencedAsync_WhenDedicatedDatabaseConfigurationFails_DeletesNothing()
    {
        var storage = new RecordingCentralFileStorage();
        var reconciler = new StoredFileReferenceReconciler(
            new TestServiceScopeFactory(_currentUser),
            storage,
            new ThrowingTenantDatabaseConnectionResolver(
                CreateConnectionInfo($"Data Source={_databasePath}")),
            _revisionClock);

        await reconciler.DeleteUnreferencedAsync(
            ["store/configuration-failure-orphan.pdf"],
            CancellationToken.None);

        Assert.Empty(storage.DeletedPaths);
    }

    [Fact]
    public async Task DeleteUnreferencedAsync_WhenLookupFails_DoesNotLogSensitiveExceptionDetails()
    {
        const string sensitiveMarker =
            "secret-path=/storage/files/private connection=Host=private-db";
        var storage = new RecordingCentralFileStorage();
        var logger = new RecordingLogger<StoredFileReferenceReconciler>();
        var reconciler = new StoredFileReferenceReconciler(
            new TestServiceScopeFactory(_currentUser),
            storage,
            new ThrowingTenantDatabaseConnectionResolver(
                CreateConnectionInfo($"Data Source={_databasePath}"),
                sensitiveMarker),
            _revisionClock,
            logger);

        await reconciler.DeleteUnreferencedAsync(
            ["store/configuration-failure-orphan.pdf"],
            CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("InvalidOperationException", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMarker, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-db", entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
        Assert.Empty(storage.DeletedPaths);
    }

    [Fact]
    public async Task DeleteUnreferencedAsync_PreservesPathReferencedOnlyByDedicatedDatabase_AndDeduplicatesEquivalentConnections()
    {
        var dedicatedDatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-file-reconciler-dedicated-{Guid.NewGuid():N}.db");
        const string dedicatedReference = "store/dedicated-reference.pdf";
        const string orphanPath = "store/two-database-orphan.pdf";
        try
        {
            var dedicatedConnectionString = $"Data Source={dedicatedDatabasePath}";
            await using (var dedicatedDbContext = CreateFileDbContext(dedicatedConnectionString))
            {
                await dedicatedDbContext.Database.EnsureCreatedAsync();
                var customer = new Customer
                {
                    Id = Guid.NewGuid(),
                    NameOriginal = "dedicated reference customer",
                    NameMatchKey = "DEDICATEDREFERENCECUSTOMER",
                    TradeType = "Sales"
                };
                dedicatedDbContext.AddRange(
                    customer,
                    new CustomerContract
                    {
                        Id = Guid.NewGuid(),
                        CustomerId = customer.Id,
                        StoragePath = dedicatedReference
                    });
                await dedicatedDbContext.SaveChangesAsync();
            }

            var firstConnection = CreateConnectionInfo(dedicatedConnectionString);
            var equivalentConnection = CreateConnectionInfo(
                $"Data Source={dedicatedDatabasePath};Default Timeout=30");
            Assert.Equal(
                PhysicalDatabaseIdentity.FromConnectionInfo(firstConnection),
                PhysicalDatabaseIdentity.FromConnectionInfo(equivalentConnection));

            var storage = new RecordingCentralFileStorage();
            var reconciler = CreateReconciler(
                storage,
                [firstConnection, equivalentConnection]);

            await reconciler.DeleteUnreferencedAsync(
                [dedicatedReference, orphanPath],
                CancellationToken.None);

            Assert.Equal([orphanPath], storage.DeletedPaths);
        }
        finally
        {
            DeleteSqliteFiles(dedicatedDatabasePath);
        }
    }

    [Fact]
    public async Task DeleteUnreferencedAsync_UsesHostFileSystemCaseSemanticsAcrossDedicatedDatabase()
    {
        var dedicatedDatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-file-reconciler-case-{Guid.NewGuid():N}.db");
        const string storedReference = "store/Case-Sensitive-Reference.pdf";
        const string differentlyCasedCandidate = "STORE/case-sensitive-reference.PDF";
        try
        {
            var dedicatedConnectionString = $"Data Source={dedicatedDatabasePath}";
            await using (var dedicatedDbContext = CreateFileDbContext(dedicatedConnectionString))
            {
                await dedicatedDbContext.Database.EnsureCreatedAsync();
                var customer = new Customer
                {
                    Id = Guid.NewGuid(),
                    NameOriginal = "dedicated case reference customer",
                    NameMatchKey = "DEDICATEDCASEREFERENCECUSTOMER",
                    TradeType = "Sales"
                };
                dedicatedDbContext.AddRange(
                    customer,
                    new CustomerContract
                    {
                        Id = Guid.NewGuid(),
                        CustomerId = customer.Id,
                        StoragePath = storedReference
                    });
                await dedicatedDbContext.SaveChangesAsync();
            }

            var storage = new RecordingCentralFileStorage();
            var reconciler = CreateReconciler(
                storage,
                [CreateConnectionInfo(dedicatedConnectionString)]);

            await reconciler.DeleteUnreferencedAsync(
                [differentlyCasedCandidate],
                CancellationToken.None);

            if (OperatingSystem.IsWindows())
                Assert.Empty(storage.DeletedPaths);
            else
                Assert.Equal([differentlyCasedCandidate], storage.DeletedPaths);
        }
        finally
        {
            DeleteSqliteFiles(dedicatedDatabasePath);
        }
    }

    [Fact]
    public async Task DeleteUnreferencedAsync_DeduplicatesCandidatesUsingHostFileSystemCaseSemantics()
    {
        const string firstCandidate = "store/Case-Orphan.pdf";
        const string secondCandidate = "STORE/case-orphan.PDF";
        var storage = new RecordingCentralFileStorage();
        var reconciler = CreateReconciler(storage);

        await reconciler.DeleteUnreferencedAsync(
            [firstCandidate, secondCandidate],
            CancellationToken.None);

        if (OperatingSystem.IsWindows())
            Assert.Equal([firstCandidate], storage.DeletedPaths);
        else
            Assert.Equal([firstCandidate, secondCandidate], storage.DeletedPaths);
    }

    [Fact]
    public async Task LegacyBlobMigration_UsesPhysicalDatabaseNamespace_AndPreservesExistingStoragePath()
    {
        var firstDatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-file-migration-first-{Guid.NewGuid():N}.db");
        var secondDatabasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-file-migration-second-{Guid.NewGuid():N}.db");
        var sharedCustomerId = Guid.NewGuid();
        var sharedContractId = Guid.NewGuid();
        var preservedContractId = Guid.NewGuid();
        var storage = new RecordingCentralFileStorage();
        try
        {
            await using (var firstDbContext = CreateFileDbContext($"Data Source={firstDatabasePath}"))
            {
                await firstDbContext.Database.EnsureCreatedAsync();
                firstDbContext.AddRange(
                    new Customer
                    {
                        Id = sharedCustomerId,
                        NameOriginal = "migration customer",
                        NameMatchKey = "MIGRATIONCUSTOMER",
                        TradeType = "Sales"
                    },
                    new CustomerContract
                    {
                        Id = sharedContractId,
                        CustomerId = sharedCustomerId,
                        FileName = "same.pdf",
                        FileContent = [1, 2, 3]
                    },
                    new CustomerContract
                    {
                        Id = preservedContractId,
                        CustomerId = sharedCustomerId,
                        FileName = "existing.pdf",
                        StoragePath = "store/existing-valid.pdf",
                        FileContent = [7, 8, 9]
                    });
                await firstDbContext.SaveChangesAsync();
                await InvokeStoredFileMigrationAsync(firstDbContext, storage);
                await firstDbContext.SaveChangesAsync();
            }

            await using (var secondDbContext = CreateFileDbContext($"Data Source={secondDatabasePath}"))
            {
                await secondDbContext.Database.EnsureCreatedAsync();
                secondDbContext.AddRange(
                    new Customer
                    {
                        Id = sharedCustomerId,
                        NameOriginal = "migration customer",
                        NameMatchKey = "MIGRATIONCUSTOMER",
                        TradeType = "Sales"
                    },
                    new CustomerContract
                    {
                        Id = sharedContractId,
                        CustomerId = sharedCustomerId,
                        FileName = "same.pdf",
                        FileContent = [4, 5, 6]
                    });
                await secondDbContext.SaveChangesAsync();
                await InvokeStoredFileMigrationAsync(secondDbContext, storage);
                await secondDbContext.SaveChangesAsync();
            }

            Assert.Equal(2, storage.SavedPaths.Count);
            Assert.NotEqual(storage.SavedPaths[0], storage.SavedPaths[1]);
            Assert.Contains("db-", storage.SavedPaths[0], StringComparison.Ordinal);
            Assert.Contains("db-", storage.SavedPaths[1], StringComparison.Ordinal);

            await using var verificationDbContext = CreateFileDbContext($"Data Source={firstDatabasePath}");
            var preserved = await verificationDbContext.CustomerContracts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == preservedContractId);
            Assert.Equal("store/existing-valid.pdf", preserved.StoragePath);
            Assert.Equal([7, 8, 9], preserved.FileContent);
        }
        finally
        {
            DeleteSqliteFiles(firstDatabasePath);
            DeleteSqliteFiles(secondDatabasePath);
        }
    }

    [Fact]
    public async Task PaymentUpload_WhenCommitCompletedThenThrew_PreservesReferencedRequestUniqueFile()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "commit ambiguity customer",
            NameMatchKey = "COMMITAMBIGUITYCUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            InvoiceNumber = "COMMIT-AMBIGUITY-001"
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            Amount = 1m
        };
        await using (var seedDbContext = CreateDbContext())
        {
            seedDbContext.AddRange(customer, invoice, payment);
            await seedDbContext.SaveChangesAsync();
        }

        var storage = new RecordingCentralFileStorage();
        var reconciler = CreateReconciler(storage);
        var commitInterceptor = new ThrowAfterCommitInterceptor();
        await using var operationDbContext = CreateDbContext(commitInterceptor);
        var controller = new PaymentsController(
            operationDbContext,
            new OfficeScopeService(_currentUser, operationDbContext),
            storage,
            reconciler,
            new RentalSettlementRecalculationService(operationDbContext));
        var clientAttachmentId = Guid.NewGuid();
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "%PDF-1.4\n% committed attachment\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
        var formFile = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "receipt.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };

        var response = await controller.UploadAttachment(
            payment.Id,
            formFile,
            "receipt",
            "commit ambiguity",
            clientAttachmentId,
            CancellationToken.None);

        await using var verificationDbContext = CreateDbContext();
        var stored = await verificationDbContext.PaymentAttachments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == clientAttachmentId);
        Assert.Single(storage.SavedFileIds);
        Assert.NotEqual(clientAttachmentId, storage.SavedFileIds[0]);
        Assert.Equal(1, commitInterceptor.CommitCount);
        Assert.DoesNotContain(stored.StoragePath, storage.DeletedPaths);
        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response.Result);
        var recovered = Assert.IsType<PaymentAttachmentDto>(ok.Value);
        Assert.Equal(stored.Id, recovered.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPaymentUploads_WithSameClientAttachmentId_PreserveExactReplayAndRejectDifferentPayload(
        bool exactReplay)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-payment-attachment-race-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=10";
        try
        {
            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                NameOriginal = "concurrent upload customer",
                NameMatchKey = "CONCURRENTUPLOADCUSTOMER",
                TradeType = "Sales"
            };
            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                InvoiceNumber = "CONCURRENT-UPLOAD-001"
            };
            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                Amount = 1m
            };
            await using (var seedDbContext = CreateFileDbContext(connectionString))
            {
                await seedDbContext.Database.EnsureCreatedAsync();
                seedDbContext.AddRange(customer, invoice, payment);
                await seedDbContext.SaveChangesAsync();
            }

            var firstSaveEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSave = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var storage = new RecordingCentralFileStorage(
                firstSaveEntered,
                releaseFirstSave);
            var reconciler = new StoredFileReferenceReconciler(
                new TestServiceScopeFactory(_currentUser),
                storage,
                new TestTenantDatabaseConnectionResolver(
                    CreateConnectionInfo(connectionString)),
                _revisionClock);
            await using var firstDbContext = CreateFileDbContext(connectionString);
            await using var secondDbContext = CreateFileDbContext(connectionString);
            var firstController = new PaymentsController(
                firstDbContext,
                new OfficeScopeService(_currentUser, firstDbContext),
                storage,
                reconciler,
                new RentalSettlementRecalculationService(firstDbContext));
            var secondController = new PaymentsController(
                secondDbContext,
                new OfficeScopeService(_currentUser, secondDbContext),
                storage,
                reconciler,
                new RentalSettlementRecalculationService(secondDbContext));
            var clientAttachmentId = Guid.NewGuid();
            var firstFileName = exactReplay ? "second.pdf" : "first.pdf";
            var firstContent = exactReplay ? "second winner" : "first contender";
            var firstDescription = exactReplay ? "second winner" : "first contender";

            var firstUpload = firstController.UploadAttachment(
                payment.Id,
                CreatePdfFormFile(firstFileName, firstContent),
                "receipt",
                firstDescription,
                clientAttachmentId,
                CancellationToken.None);
            await firstSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Microsoft.AspNetCore.Mvc.ActionResult<PaymentAttachmentDto> secondResponse;
            try
            {
                secondResponse = await secondController.UploadAttachment(
                    payment.Id,
                    CreatePdfFormFile("second.pdf", "second winner"),
                    "receipt",
                    "second winner",
                    clientAttachmentId,
                    CancellationToken.None);
            }
            finally
            {
                releaseFirstSave.TrySetResult();
            }
            var firstResponse = await firstUpload;

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                secondResponse.Result);
            var winner = Assert.IsType<PaymentAttachmentDto>(ok.Value);
            Assert.Equal(clientAttachmentId, winner.Id);
            if (exactReplay)
            {
                var firstOk = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                    firstResponse.Result);
                var recoveredWinner = Assert.IsType<PaymentAttachmentDto>(firstOk.Value);
                Assert.Equal(clientAttachmentId, recoveredWinner.Id);
                Assert.Equal(winner.FileHash, recoveredWinner.FileHash);
                Assert.Equal(winner.FileSize, recoveredWinner.FileSize);
                Assert.Equal(winner.FileName, recoveredWinner.FileName);
                Assert.Equal(winner.AttachmentType, recoveredWinner.AttachmentType);
                Assert.Equal(winner.Description, recoveredWinner.Description);
            }
            else
            {
                var firstConflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(
                    firstResponse.Result);
                Assert.Contains(
                    "client_attachment_payload_conflict",
                    System.Text.Json.JsonSerializer.Serialize(firstConflict.Value),
                    StringComparison.Ordinal);
            }

            await using var verificationDbContext = CreateFileDbContext(connectionString);
            var stored = await verificationDbContext.PaymentAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == clientAttachmentId);
            Assert.Equal(2, storage.SavedFileIds.Count);
            Assert.Equal(2, storage.SavedFileIds.Distinct().Count());
            Assert.DoesNotContain(clientAttachmentId, storage.SavedFileIds);
            Assert.Contains(storage.SavedPaths[0], storage.DeletedPaths);
            Assert.DoesNotContain(stored.StoragePath, storage.DeletedPaths);
            Assert.Equal(storage.SavedPaths[1], stored.StoragePath);
            Assert.True(FileContentIntegrityVerifier.HasExpectedIntegrity(
                storage.ReadBytes(stored.StoragePath, stored.FileContent),
                stored.FileSize,
                stored.FileHash));
        }
        finally
        {
            foreach (var candidate in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                try
                {
                    File.Delete(candidate);
                }
                catch
                {
                    // Test cleanup only.
                }
            }
        }
    }

    private StoredFileReferenceReconciler CreateReconciler(
        ICentralFileStorage storage,
        IReadOnlyList<TenantDatabaseConnectionInfo>? dedicatedConnections = null)
        => new(
            new TestServiceScopeFactory(_currentUser),
            storage,
            new TestTenantDatabaseConnectionResolver(
                CreateConnectionInfo($"Data Source={_databasePath}"),
                dedicatedConnections),
            _revisionClock);

    private AppDbContext CreateDbContext(IInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection);
        if (interceptor is not null)
            optionsBuilder.AddInterceptors(interceptor);

        return new AppDbContext(optionsBuilder.Options, _currentUser, _revisionClock);
    }

    private AppDbContext CreateFileDbContext(
        string connectionString,
        IInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString);
        if (interceptor is not null)
            optionsBuilder.AddInterceptors(interceptor);

        return new AppDbContext(optionsBuilder.Options, _currentUser, _revisionClock);
    }

    private static IFormFile CreatePdfFormFile(string fileName, string marker)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $"%PDF-1.4\n% {marker}\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };
    }

    private static async Task InvokeStoredFileMigrationAsync(
        AppDbContext dbContext,
        ICentralFileStorage storage)
    {
        var method = typeof(DbInitializer).GetMethod(
            "MigrateStoredFilesToCentralStorageAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(
            method!.Invoke(null, [dbContext, storage, CancellationToken.None]));
        await task;
    }

    public void Dispose()
    {
        _connection.Dispose();
        DeleteSqliteFiles(_databasePath);
    }

    private static void DeleteSqliteFiles(string databasePath)
    {
        foreach (var candidate in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            try
            {
                File.Delete(candidate);
            }
            catch
            {
                // Test cleanup only.
            }
        }
    }

    private static TenantDatabaseConnectionInfo CreateConnectionInfo(string connectionString)
        => new()
        {
            UseSqlite = true,
            ConnectionString = connectionString,
            TenantCode = TenantScopeCatalog.UsenetGroup
        };

    private sealed class TestServiceScopeFactory(ICurrentUserContext currentUser) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => new TestServiceScope(currentUser);
    }

    private sealed class TestServiceScope(ICurrentUserContext currentUser) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new TestServiceProvider(currentUser);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class TestServiceProvider(ICurrentUserContext currentUser) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ICurrentUserContext) ? currentUser : null;
    }

    private sealed class TestTenantDatabaseConnectionResolver(
        TenantDatabaseConnectionInfo current,
        IReadOnlyList<TenantDatabaseConnectionInfo>? dedicatedConnections = null)
        : ITenantDatabaseConnectionResolver
    {
        public TenantDatabaseConnectionInfo ResolveCurrent() => current;
        public TenantDatabaseConnectionInfo ResolveCentral() => current;
        public TenantDatabaseConnectionInfo ResolveBusinessTenant(string? tenantCode) => current;
        public IReadOnlyList<TenantDatabaseConnectionInfo> GetDedicatedBusinessConnections()
            => dedicatedConnections ?? [];
    }

    private sealed class ThrowingTenantDatabaseConnectionResolver(
        TenantDatabaseConnectionInfo current,
        string message = "deterministic dedicated database configuration failure")
        : ITenantDatabaseConnectionResolver
    {
        public TenantDatabaseConnectionInfo ResolveCurrent() => current;
        public TenantDatabaseConnectionInfo ResolveCentral() => current;
        public TenantDatabaseConnectionInfo ResolveBusinessTenant(string? tenantCode) => current;
        public IReadOnlyList<TenantDatabaseConnectionInfo> GetDedicatedBusinessConnections()
            => throw new InvalidOperationException(message);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(
                logLevel,
                formatter(state, exception),
                exception));
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception);

    private sealed class ThrowAfterCommitInterceptor : DbTransactionInterceptor
    {
        public int CommitCount { get; private set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            return Task.FromException(
                new InvalidOperationException("deterministic exception after database commit"));
        }
    }

    private sealed class RecordingCentralFileStorage : ICentralFileStorage
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource? _firstSaveEntered;
        private readonly TaskCompletionSource? _releaseFirstSave;
        private readonly Dictionary<string, byte[]> _savedContentByPath = new(StringComparer.OrdinalIgnoreCase);
        private int _saveCount;

        public string RootPath => Path.GetTempPath();
        public List<string> DeletedPaths { get; } = [];
        public List<Guid> SavedFileIds { get; } = [];
        public List<string> SavedPaths { get; } = [];

        public RecordingCentralFileStorage(
            TaskCompletionSource? firstSaveEntered = null,
            TaskCompletionSource? releaseFirstSave = null)
        {
            _firstSaveEntered = firstSaveEntered;
            _releaseFirstSave = releaseFirstSave;
        }

        public async Task<string> SaveBytesAsync(
            string area,
            string ownerId,
            Guid fileId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
        {
            var storedPath = Path.Combine(
                RootPath,
                area,
                ownerId,
                $"{fileId:N}__{fileName}");
            lock (_gate)
            {
                SavedFileIds.Add(fileId);
                SavedPaths.Add(storedPath);
                _savedContentByPath[storedPath] = content.ToArray();
            }

            if (Interlocked.Increment(ref _saveCount) == 1 &&
                _firstSaveEntered is not null &&
                _releaseFirstSave is not null)
            {
                _firstSaveEntered.TrySetResult();
                await _releaseFirstSave.Task.WaitAsync(cancellationToken);
            }

            return storedPath;
        }

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
        {
            if (!string.IsNullOrWhiteSpace(storedPath))
            {
                lock (_gate)
                {
                    if (_savedContentByPath.TryGetValue(storedPath, out var content))
                        return content.ToArray();
                }
            }

            return fallback ?? [];
        }

        public void DeleteIfExists(string? storedPath)
        {
            if (!string.IsNullOrWhiteSpace(storedPath))
            {
                lock (_gate)
                {
                    DeletedPaths.Add(storedPath);
                    _savedContentByPath.Remove(storedPath);
                }
            }
        }
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Username => "reconciler-test";
        public string TenantCode => TenantScopeCatalog.UsenetGroup;
        public string OfficeCode => OfficeCodeCatalog.Usenet;
        public string ScopeType => TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => false;
        public IReadOnlyCollection<string> Permissions => [];

        public bool HasPermission(string permission)
            => true;
    }
}
